using System.Windows;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using QuickClip.Models;
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
    private readonly List<ClipboardItem> _items = new();
    private readonly object _lock = new();
    private StackHudWindow? _hud;
    private volatile bool _isActive;

    public bool IsActive => _isActive;

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

    public StackPasteService(PasteService pasteService, ToastService? toastService = null, SettingsService? settings = null)
    {
        _pasteService = pasteService;
        _toastService = toastService;
        _settings = settings;

        if (_settings != null)
        {
            _settings.Changed += OnSettingsChanged;
        }
    }

    private void OnSettingsChanged()
    {
        RunOnUi(() =>
        {
            if (_hud != null && _settings != null)
            {
                _hud.ApplySize(_settings.ToastSize);
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
                    return item.TextContent?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "(空文本)";
                })
                .ToList();
        }
    }

    /// <summary>启动收集栈模式。</summary>
    public void Start()
    {
        if (IsActive)
        {
            RunOnUi(() => _hud?.Show());
            return;
        }

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
            _hud.Show();
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
        lock (_lock)
        {
            _items.Clear();
        }

        RunOnUi(() => _hud?.Hide());

        NotifyStateChanged();

        // 仅在全部粘贴完毕自动退出时轻量提示；用户主动关闭浮窗时无需多余弹窗干扰
        if (isAutoExit)
        {
            _toastService?.Show("收集栈已全部粘贴完毕", durationSeconds: 1.2);
        }
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

        lock (_lock)
        {
            _items.Add(item);
        }

        UpdateHud();
        NotifyStateChanged();
    }

    /// <summary>将当前剪贴板文本按行拆分，每一行作为一个条目压入栈。</summary>
    public void SplitClipboardLines()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
            }
        }
        catch
        {
            // 忽略剪贴板暂态占用
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _hud?.ShowTransientFeedback("剪贴板无可拆分文本", 1.2);
            return;
        }

        string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            _hud?.ShowTransientFeedback("剪贴板无可拆分文本", 1.2);
            return;
        }

        lock (_lock)
        {
            // 若栈内最后一项正是刚复制的整段多行文本，将其移除，仅保留拆分后的各行结果
            if (_items.Count > 0 && string.Equals(_items[^1].TextContent?.Trim(), text.Trim(), StringComparison.Ordinal))
            {
                _items.RemoveAt(_items.Count - 1);
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    _items.Add(new ClipboardItem
                    {
                        ContentType = ClipboardContentType.Text,
                        TextContent = line,
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

        // 就地在浮窗内微反馈，绝不弹 Toast 遮挡视线或打断操作
        _hud?.ShowTransientFeedback($"✓ 已拆分入栈 ({lines.Length} 项)", 1.2);
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

        // 记住当前前台窗口并执行粘贴
        _pasteService.RememberTargetWindow();

        switch (item.ContentType)
        {
            case ClipboardContentType.Image:
                _pasteService.PasteImage(item.PreviewPath);
                break;
            case ClipboardContentType.File when !string.IsNullOrEmpty(item.TextContent):
                _pasteService.PasteFiles(new[] { item.TextContent });
                break;
            default:
                _pasteService.PasteText(item.TextContent, plainOnly: false, item.HtmlContent, item.RtfContent);
                break;
        }

        UpdateHud();
        NotifyStateChanged();

        if (remaining == 0)
        {
            // 全部粘贴完毕，自动退出收集栈模式
            Stop(isAutoExit: true);
        }

        return true;
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
