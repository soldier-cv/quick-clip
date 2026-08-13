using System.IO;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 剪贴板处理流水线：去重防抖、类型解析、图片二维码识别、写入数据库。
/// </summary>
public sealed class ClipboardPipeline
{
    private readonly AppPaths _paths;
    private DatabaseService _db;
    private readonly QrCodeService _qr;
    private readonly PasteService _paste;
    private readonly SettingsService _settings;
    private readonly object _lock = new();

    private string? _lastKey;
    private DateTime _lastTime = DateTime.MinValue;
    /// <summary>自身回写系统剪贴板后的忽略截止时间（UTC）。</summary>
    private DateTime _suppressUntilUtc = DateTime.MinValue;

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

    /// <summary>设置里切换数据库后替换存储目标（旧连接由调用方负责释放）。</summary>
    public void AttachDatabase(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// 应用从历史「复制/粘贴」回写系统剪贴板前调用：短时间内忽略捕获，
    /// 避免同一条内容再插到列表第一行。
    /// </summary>
    public void SuppressCapture(string? dedupKey = null, int milliseconds = 2500)
    {
        lock (_lock)
        {
            _suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(500, milliseconds));
            if (!string.IsNullOrEmpty(dedupKey))
            {
                _lastKey = dedupKey;
                _lastTime = DateTime.Now;
            }
        }
    }

    private bool ShouldIgnoreCapture()
    {
        if (_paste.IsSelfPasting)
        {
            return true;
        }

        lock (_lock)
        {
            return DateTime.UtcNow < _suppressUntilUtc;
        }
    }

    /// <summary>
    /// 剪贴板变化回调（UI 线程进入，内部异步处理）。
    /// 只读系统剪贴板并可选写入本地历史；任何超限/跳过都不会 Clear 或改写系统剪贴板，
    /// 用户仍可把刚复制的内容粘贴到其他程序。
    /// </summary>
    public async void OnClipboardUpdated()
    {
        // 自身回写 / 抑制窗口内：不入库（列表点选复制、双击粘贴等）
        if (ShouldIgnoreCapture())
        {
            return;
        }

        try
        {
            // 剪贴板读取必须在 STA 线程（只读 Capture，不写回系统剪贴板）
            var data = await StaTask.Run(() => ClipboardDataExtractor.Capture(_paths));

            // await 之后再判一次：抑制窗口可能覆盖异步空档
            if (data == null || ShouldIgnoreCapture())
            {
                if (data?.PreviewPath is { } orphan)
                {
                    TryDeletePreview(orphan);
                }

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
                // 短时间相同内容去重（连续复制同一段 / 历史回写）
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
                PreviewPath = data.PreviewPath,
                QrContent = qr,
                CharCount = data.CharCount,
                CreatedAt = DateTime.Now
            };

            await _db.InsertAsync(item);

            var trimmed = await _db.TrimToMaxItemsAsync(_settings.MaxHistoryItems);
            foreach (var (_, preview) in trimmed)
            {
                ThumbnailCache.RemoveByPath(preview);
                if (!string.IsNullOrEmpty(preview))
                {
                    try { File.Delete(preview); } catch { /* ignore */ }
                }
            }

            ItemAdded?.Invoke(item);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("剪贴板流水线处理失败", ex);
        }
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
