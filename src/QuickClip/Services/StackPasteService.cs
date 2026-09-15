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
    private readonly Queue<ClipboardItem> _queue = new();
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
                return _queue.Count;
            }
        }
    }

    public event Action<bool, int>? StateChanged;

    public StackPasteService(PasteService pasteService)
    {
        _pasteService = pasteService;
    }

    /// <summary>启动收集栈模式。</summary>
    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        _isActive = true;
        RunOnUi(() =>
        {
            if (_hud == null)
            {
                _hud = new StackHudWindow();
                _hud.SplitLinesRequested += SplitClipboardLines;
                _hud.ClearRequested += Clear;
                _hud.ExitRequested += Stop;
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
            _hud.Show();
            UpdateHud();
        });

        NotifyStateChanged();
    }

    /// <summary>退出收集栈模式并清空栈。</summary>
    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        _isActive = false;
        lock (_lock)
        {
            _queue.Clear();
        }

        RunOnUi(() => _hud?.Hide());

        NotifyStateChanged();
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
            _queue.Clear();
        }
        UpdateHud();
        NotifyStateChanged();
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
            _queue.Enqueue(item);
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
            return;
        }

        string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    _queue.Enqueue(new ClipboardItem
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
            if (_queue.Count > 0)
            {
                item = _queue.Dequeue();
                remaining = _queue.Count;
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
            Stop();
        }

        return true;
    }

    private void UpdateHud()
    {
        int count;
        string? nextPreview = null;

        lock (_lock)
        {
            count = _queue.Count;
            if (_queue.Count > 0)
            {
                nextPreview = _queue.Peek().TextContent;
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
