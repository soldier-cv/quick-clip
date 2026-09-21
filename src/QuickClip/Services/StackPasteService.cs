using System.Windows;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using QuickClip.Models;
using QuickClip.Native;
using QuickClip.Views;

namespace QuickClip.Services;

/// <summary>
/// 收集栈服务（批量顺次粘贴）：
/// 开启后，用户多次复制的内容自动压入先进先出（FIFO）队列；
/// 随后在目标程序中按 Ctrl+V 依次出栈粘贴，直到栈清空自动退出。
/// </summary>
public sealed class StackPasteService : IDisposable
{
    private readonly PasteService _pasteService;
    private readonly ToastService? _toastService;
    private readonly SettingsService? _settings;
    private readonly ClipboardPipeline? _pipeline;
    private readonly List<ClipboardItem> _items = new();
    private readonly object _lock = new();
    private StackHudWindow? _hud;
    private volatile bool _isActive;
    private volatile bool _lastPasteInFlight;
    private volatile bool _shouldSuppressPostStackPaste;
    private int _session;
    private Window? _panelWindow;

    public bool IsActive => _isActive;

    /// <summary>最后一条已出栈、目标窗口尚未读完剪贴板。</summary>
    public bool LastPasteInFlight => _lastPasteInFlight;

    /// <summary>收集栈最后一条出栈后，防重复粘贴守卫是否生效。</summary>
    public bool ShouldSuppressPostStackPaste => _shouldSuppressPostStackPaste;

    /// <summary>重置出栈后的 Ctrl+V 拦截守卫（用户新复制内容或开启新会话时调用）。</summary>
    public void ResetPostStackSuppression()
    {
        _shouldSuppressPostStackPaste = false;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public event Action<bool, int>? StateChanged;

    public StackPasteService(
        PasteService pasteService,
        ToastService? toastService = null,
        SettingsService? settings = null,
        ClipboardPipeline? pipeline = null)
    {
        _pasteService = pasteService;
        _toastService = toastService;
        _settings = settings;
        _pipeline = pipeline;

        if (_settings != null)
        {
            _settings.Changed += OnSettingsChanged;
        }
    }

    /// <summary>绑定主列表面板，供 HUD 跟宽与磁吸。</summary>
    public void AttachPanel(Window panel)
    {
        _panelWindow = panel;
        panel.IsVisibleChanged += (_, _) => SyncHudToPanel();
    }

    /// <summary>主面板移动/缩放/显隐时同步 HUD（用户拖开 HUD 后只跟宽度）。</summary>
    public void SyncHudToPanel()
    {
        if (!IsActive)
        {
            return;
        }

        RunOnUi(() => _hud?.FollowPanel(_panelWindow));
    }

    private void OnSettingsChanged()
    {
        RunOnUi(() =>
        {
            if (_hud != null)
            {
                if (_settings != null)
                {
                    _hud.ApplySize(_settings.ToastSize);
                }

                _hud.FollowPanel(_panelWindow);
            }
        });
    }

    /// <summary>获取栈内当前排队的前 N 个条目文本快照（供悬停预览）。</summary>
    public List<string> GetSnapshot(int maxCount = 10)
    {
        lock (_lock)
        {
            return _items
                .Take(maxCount)
                .Select(item =>
                {
                    if (item.ContentType == ClipboardContentType.Image)
                    {
                        return "[图片]";
                    }
                    if (item.ContentType == ClipboardContentType.File)
                    {
                        return $"[文件] {item.TextContent}";
                    }
                    return item.TextContent?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "（空文本）";
                })
                .ToList();
        }
    }

    /// <summary>启动收集栈模式。</summary>
    public void Start()
    {
        if (IsActive)
        {
            RunOnUi(() =>
            {
                _hud?.ResetDock();
                _hud?.Show();
                _hud?.FollowPanel(_panelWindow);
            });
            return;
        }

        Interlocked.Increment(ref _session);
        _lastPasteInFlight = false;
        _shouldSuppressPostStackPaste = false;
        _isActive = true;
        RunOnUi(() =>
        {
            if (_hud == null)
            {
                _hud = new StackHudWindow();
                if (_settings != null)
                {
                    _hud.ApplySize(_settings.ToastSize);
                }

                _hud.QueueSnapshotProvider = () => GetSnapshot(10);
                _hud.SplitLinesRequested += SplitClipboardLines;
                _hud.ClearRequested += Clear;
                _hud.ExitRequested += () => Stop();
                _hud.Closed += (s, e) =>
                {
                    _hud = null;
                    if (IsActive)
                    {
                        // 用户直接关闭 HUD（如 Alt+F4）时同步退出收集栈，避免状态与界面脱节
                        Stop();
                    }
                };
            }
            else if (_settings != null)
            {
                _hud.ApplySize(_settings.ToastSize);
            }
            _hud.ResetDock();
            _hud.Show();
            _hud.FollowPanel(_panelWindow);
            UpdateHud();
        });

        NotifyStateChanged();
        // 开启收集栈时悬浮窗本身浮现即为最直接的反馈，不额外发 Toast 避免右下角重叠遮挡
    }

    /// <summary>退出收集栈模式并清空栈。</summary>
    public void Stop(bool isAutoExit = false)
    {
        if (!IsActive)
        {
            return;
        }

        _isActive = false;
        _lastPasteInFlight = false;
        _shouldSuppressPostStackPaste = false;
        Interlocked.Increment(ref _session);
        lock (_lock)
        {
            _items.Clear();
        }

        HideHudWithoutActivating();
        ClearSystemClipboardQuietly();
        NotifyStateChanged();

        // 仅在全部粘贴完毕自动退出时轻量提示；用户主动关闭浮窗时无需多余弹窗干扰
        if (isAutoExit)
        {
            _toastService?.Show("收集栈已全部粘贴完毕", durationSeconds: 1.2);
        }
    }

    private void ClearSystemClipboardQuietly()
    {
        _ = StaTask.Run(() =>
        {
            if (NativeClipboard.TryClear())
            {
                _pasteService.MarkOwnSequence();
            }

            return true;
        });
    }

    private void HideHudWithoutActivating()
    {
        RunOnUi(() =>
        {
            if (_hud == null)
            {
                return;
            }

            _hud.ShowActivated = false;
            _hud.Hide();
        });
    }

    /// <summary>切换收集栈启用/停用状态。</summary>
    public void Toggle()
    {
        if (IsActive)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>清空栈内条目（保持栈模式开启）。</summary>
    public void Clear()
    {
        _shouldSuppressPostStackPaste = false;
        lock (_lock)
        {
            _items.Clear();
        }
        UpdateHud();
        NotifyStateChanged();
        _hud?.ShowTransientFeedback("已清空收集栈", 1.0);
    }

    /// <summary>将条目压入收集栈。</summary>
    public void Push(ClipboardItem item)
    {
        if (!IsActive || item == null)
        {
            return;
        }

        _shouldSuppressPostStackPaste = false;
        lock (_lock)
        {
            _items.Add(item);
        }

        UpdateHud();
        NotifyStateChanged();
    }

    /// <summary>将收集栈内最新多行条目或剪贴板多行文本按换行拆分成多项。</summary>
    public void SplitClipboardLines()
    {
        string? text = null;
        int targetIndex = -1;

        lock (_lock)
        {
            // 1. 优先在当前栈内查找包含换行的条目（从最后入栈的一项往前找）
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var content = _items[i].TextContent;
                if (!string.IsNullOrWhiteSpace(content) && (content.Contains('\n') || content.Contains('\r')))
                {
                    text = content;
                    targetIndex = i;
                    break;
                }
            }
        }

        // 2. 若栈内没有多行条目，则尝试从系统剪贴板读取
        if (string.IsNullOrWhiteSpace(text))
        {
            text = NativeClipboard.TryGetText();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _hud?.ShowTransientFeedback("无可拆分的文本内容", 1.2);
            return;
        }

        // 按行拆分（去除空行与首尾空白）
        string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim())
                             .Where(l => !string.IsNullOrEmpty(l))
                             .ToArray();

        if (lines.Length <= 1)
        {
            _hud?.ShowTransientFeedback("文本仅有一行，无需拆分", 1.2);
            return;
        }

        lock (_lock)
        {
            // 若是从栈内某一项拆分出来的，先移除该多行项，并在原位置展开各行
            if (targetIndex >= 0 && targetIndex < _items.Count)
            {
                _items.RemoveAt(targetIndex);
                int insertPos = targetIndex;
                foreach (string line in lines)
                {
                    _items.Insert(insertPos++, new ClipboardItem
                    {
                        ContentType = ClipboardContentType.Text,
                        TextContent = line,
                        CharCount = line.Length,
                        CreatedAt = DateTime.Now
                    });
                }
            }
            else
            {
                // 若栈内原本没有该多行项（如直接读取自剪贴板），直接追加到栈末尾
                foreach (string line in lines)
                {
                    _items.Add(new ClipboardItem
                    {
                        ContentType = ClipboardContentType.Text,
                        TextContent = line,
                        CharCount = line.Length,
                        CreatedAt = DateTime.Now
                    });
                }
            }
        }

        if (!IsActive)
        {
            Start();
        }
        else
        {
            UpdateHud();
            NotifyStateChanged();
        }

        _hud?.ShowTransientFeedback($"已按行拆分（{lines.Length} 项）", 1.2);
    }

    /// <summary>
    /// 出栈一个条目并模拟粘贴到目标窗口。
    /// 返回 true 表示成功出栈粘贴；返回 false 表示栈已空。
    /// </summary>
    public bool PopAndPaste()
    {
        ClipboardItem? item = null;
        int remaining = 0;

        lock (_lock)
        {
            if (_items.Count > 0)
            {
                item = _items[0];
                _items.RemoveAt(0);
                remaining = _items.Count;
            }
        }

        if (item == null)
        {
            Stop();
            return false;
        }

        Task pasteTask = item.ContentType switch
        {
            ClipboardContentType.Image =>
                _pasteService.PasteImageAsync(item.PreviewPath, stayOnForeground: true),
            ClipboardContentType.File when !string.IsNullOrEmpty(item.TextContent) =>
                _pasteService.PasteFilesAsync(new[] { item.TextContent }, stayOnForeground: true),
            _ => _pasteService.PasteTextAsync(
                item.TextContent, plainOnly: false, item.HtmlContent, item.RtfContent, stayOnForeground: true)
        };

        if (remaining == 0)
        {
            // 立刻停用，避免延迟退出把这期间新复制的内容一并清掉；
            // 剪贴板必须等目标窗口读完再 Empty，否则最后一条 Ctrl+V 会贴成空
            int session = Volatile.Read(ref _session);
            _isActive = false;
            _lastPasteInFlight = true;
            _shouldSuppressPostStackPaste = true;
            HideHudWithoutActivating();
            NotifyStateChanged();
            _ = FinishLastItemAsync(pasteTask, session);
        }
        else
        {
            UpdateHud();
            NotifyStateChanged();
        }

        return true;
    }

    private async Task FinishLastItemAsync(Task pasteTask, int session)
    {
        try
        {
            await pasteTask.ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
        }
        catch
        {
            // 粘贴失败仍清剪贴板，避免残留最后一条被再次 Ctrl+V
        }

        if (session != Volatile.Read(ref _session))
        {
            return;
        }

        _lastPasteInFlight = false;
        // 带轻量重试清空系统剪贴板（避免目标窗口短暂占用导致清空失败）
        for (int i = 0; i < 3; i++)
        {
            bool cleared = await StaTask.Run(() =>
            {
                if (NativeClipboard.TryClear())
                {
                    _pasteService.MarkOwnSequence();
                    return true;
                }
                return false;
            });

            if (cleared)
            {
                break;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private void UpdateHud()
    {
        int count;
        string? nextPreview = null;

        lock (_lock)
        {
            count = _items.Count;
            if (_items.Count > 0)
            {
                nextPreview = _items[0].TextContent;
            }
        }

        var hud = _hud;
        hud?.UpdateState(count, nextPreview);
    }

    /// <summary>在 UI 线程执行动作；不在 UI 线程时改为异步投递，避免阻塞键盘钩子回调。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(IsActive, Count);
    }

    public void Dispose()
    {
        Stop();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                _hud?.Close();
                _hud = null;
            }
            else if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.Invoke(() =>
                {
                    _hud?.Close();
                    _hud = null;
                });
            }
        }
        catch
        {
            // 应用退出阶段忽略窗口关闭异常
        }
    }
}
