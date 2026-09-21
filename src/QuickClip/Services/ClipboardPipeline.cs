using System.IO;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 剪贴板处理流水线：去重防抖、类型解析、图片二维码识别、写入数据库。
///
/// 自身回写靠「剪贴板序列号」识别（<see cref="PasteService.IsOwnSequence"/>）：
/// 只有序列号等于自己刚写入的那次才跳过，因此用户在 2.5 秒内的真实复制不会再被丢掉。
/// </summary>
public sealed class ClipboardPipeline
{
    private readonly AppPaths _paths;
    private readonly DatabaseService _db;
    private readonly QrCodeService _qr;
    private readonly PasteService _paste;
    private readonly SettingsService _settings;
    private readonly object _lock = new();

    /// <summary>串行化捕获：连续剪贴板通知不再并发跑多条流水线。</summary>
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    private string? _lastKey;
    private DateTime _lastTime = DateTime.MinValue;

    /// <summary>
    /// 捕获超时看门狗：属主进程延迟渲染卡死时原生 GetClipboardData 仍可能阻塞，
    /// 不能无限等（被放弃的线程仍持有剪贴板，无法从这里释放），超时即跳过本次捕获。
    /// 取值放宽到 5s：正常大图编码 + 哈希也可能耗时约 2s，避免误判。
    /// </summary>
    private const int CaptureTimeoutMilliseconds = 5000;

    /// <summary>新条目入库后触发（UI 线程）。</summary>
    public event Action<ClipboardItem>? ItemAdded;

    public ClipboardPipeline(AppPaths paths, DatabaseService db, QrCodeService qr, PasteService paste, SettingsService settings)
    {
        _paths = paths;
        _db = db;
        _qr = qr;
        _paste = paste;
        _settings = settings;
    }

    /// <summary>
    /// 剪贴板变化回调（UI 线程进入，内部异步处理）。
    /// 只读系统剪贴板并可选写入本地历史；任何超限/跳过都不会 Clear 或改写系统剪贴板，
    /// 用户仍可把刚复制的内容粘贴到其他程序。
    /// </summary>
    public async void OnClipboardUpdated()
    {
        // 用户主动暂停捕获（复制敏感内容时）：不读、不入库
        if (_settings.CapturePaused)
        {
            DebugLog.LogDetail("捕获已暂停，跳过本次剪贴板通知");
            return;
        }

        // 自身正在写剪贴板（同步竞态窗口）：直接跳过
        if (_paste.IsSelfPasting)
        {
            return;
        }

        // 串行化：上一个捕获还没结束时直接跳过，避免重复入库 / 预览文件互相覆盖
        if (!await _captureGate.WaitAsync(0))
        {
            DebugLog.LogDetail("已有捕获在处理，跳过本次剪贴板通知");
            return;
        }

        try
        {
            // 剪贴板读取（只读 Capture，不写回系统剪贴板）
            var dataTask = StaTask.Run(() => ClipboardDataExtractor.Capture(_paths));

            // 超时看门狗：属主进程卡死时读取可能阻塞，等待超限即放弃本次捕获
            var finished = await Task.WhenAny(dataTask, Task.Delay(CaptureTimeoutMilliseconds));
            if (finished != dataTask)
            {
                // 被放弃的任务仍可能落盘预览图：等它真正结束时回收，避免孤儿文件长期堆积
                _ = dataTask.ContinueWith(
                    t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                        {
                            TryDeletePreview(t.Result?.PreviewPath);
                        }
                    },
                    TaskScheduler.Default);
                DebugLog.Log($"剪贴板捕获超时（{CaptureTimeoutMilliseconds}ms），可能被其他进程占用，本次跳过");
                return;
            }

            var data = dataTask.Result;
            if (data == null)
            {
                return;
            }

            // 自身回写：序列号一致 → 不入库（比时间窗精确，也不会误丢用户真实复制）
            if (_paste.IsOwnSequence(data.SequenceNumber))
            {
                DebugLog.LogDetail($"忽略自身回写（序列号 {data.SequenceNumber}）");
                TryDeletePreview(data.PreviewPath);
                return;
            }

            // await 之后再判一次：抑制窗口可能覆盖异步空档
            if (_paste.IsSelfPasting)
            {
                TryDeletePreview(data.PreviewPath);
                return;
            }

            // 仅文本模式：跳过图片 / 文件入库（系统侧仍可粘贴）
            if (_settings.TextOnlyCapture &&
                data.ContentType is ClipboardContentType.Image or ClipboardContentType.File)
            {
                TryDeletePreview(data.PreviewPath);
                return;
            }

            lock (_lock)
            {
                var now = DateTime.Now;
                // 短时间相同内容去重（连续复制同一段 / 自身回写兜底）
                if (_lastKey == data.DedupKey && (now - _lastTime).TotalSeconds < 8)
                {
                    TryDeletePreview(data.PreviewPath);
                    return;
                }

                _lastKey = data.DedupKey;
                _lastTime = now;
            }

            // 图片在后台自动识别二维码
            string? qr = null;
            if (data.ContentType == ClipboardContentType.Image && !string.IsNullOrEmpty(data.PreviewPath))
            {
                qr = await Task.Run(() => _qr.Decode(data.PreviewPath!));
            }

            var item = new ClipboardItem
            {
                ContentType = data.ContentType,
                TextContent = data.ContentType == ClipboardContentType.File
                    ? string.Join(Environment.NewLine, data.Files ?? Array.Empty<string>())
                    : data.Text,
                HtmlContent = data.Html,
                RtfContent = data.Rtf,
                PreviewPath = data.PreviewPath,
                QrContent = qr,
                CharCount = data.CharCount,
                CreatedAt = DateTime.Now
            };

            var (id, isNew) = await _db.UpsertRecentAsync(item);
            if (!isNew && !string.IsNullOrEmpty(data.PreviewPath) && data.PreviewPath != item.PreviewPath)
            {
                TryDeletePreview(data.PreviewPath);
            }

            // 超条数清理交由后台定时轮询（每 15 分钟）与设置变更时统一处理，避免在复制热路径上执行额外 I/O
            ItemAdded?.Invoke(item);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("剪贴板流水线处理失败", ex);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>将一条纯文本记入历史（收集栈拆分行）；不改系统剪贴板。</summary>
    public async Task<ClipboardItem?> RecordTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var item = new ClipboardItem
        {
            ContentType = ClipboardContentType.Text,
            TextContent = text,
            CharCount = text.Length,
            CreatedAt = DateTime.Now
        };

        await _db.UpsertRecentAsync(item);
        await _db.TrimToMaxItemsAsync(_settings.MaxHistoryItems);
        ItemAdded?.Invoke(item);
        return item;
    }

    private static void TryDeletePreview(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
