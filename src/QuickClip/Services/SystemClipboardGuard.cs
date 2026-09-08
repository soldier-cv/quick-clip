using System.Text;
using System.Threading;
using System.Windows.Threading;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>
/// 系统剪贴板历史窗口兜底守卫。
///
/// 低级键盘钩子（WH_KEYBOARD_LL）受 Windows UIPI 权限隔离限制，无法收到「以管理员权限运行」的
/// 窗口（如管理员终端）的键盘输入；此时 Win+V 会漏给系统，弹出系统剪贴板历史。
/// 本守卫以低频轮询监视前台窗口：一旦发现系统剪贴板历史窗口弹出，立即注入 ESC 关闭它，
/// 随后唤起 QuickClip 面板，保证在任意窗口下按 Win+V 最终都落到 QuickClip。
/// 钩子正常接管时系统剪贴板历史根本不会出现，本守卫零干扰；仅作为钩子失效时的兜底。
///
/// 防自激：每个窗口句柄只处理一次，句柄消失后才从已处理集合移除，
/// 避免「ESC 没能关掉窗口」时每 5 秒注入一次 ESC 并反复切换面板。
/// </summary>
public sealed class SystemClipboardGuard : IDisposable
{
    /// <summary>系统剪贴板历史窗口的窗口类名（XAML CoreWindow）。</summary>
    private const string CoreWindowClassName = "Windows.UI.Core.CoreWindow";

    /// <summary>系统剪贴板历史窗口标题关键字（中文 / 英文，不区分大小写）。</summary>
    private static readonly string[] TitleKeywords = { "剪贴板历史", "剪贴板历史记录", "Clipboard history" };

    /// <summary>轮询间隔：兼顾响应速度与 CPU 开销（约 8 次/秒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>注入 ESC 后等待窗口关闭的上限；超时说明 ESC 未生效（例如被 UIPI 拦截）。</summary>
    private static readonly TimeSpan EscapeCloseTimeout = TimeSpan.FromMilliseconds(400);

    private readonly Dispatcher _uiDispatcher;
    private readonly System.Threading.Timer _timer;
    private readonly object _handledLock = new();

    /// <summary>已处理过的系统剪贴板历史窗口句柄；窗口消失后移除，可再次处理新窗口。</summary>
    private readonly HashSet<IntPtr> _handledWindows = new();

    private volatile bool _disposed;

    /// <summary>检测到系统剪贴板历史窗口并完成接管时触发（UI 线程），用于唤起/切换面板。</summary>
    public event Action? ToggleRequested;

    public SystemClipboardGuard(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _timer = new System.Threading.Timer(Tick, null, PollInterval, PollInterval);
    }

    private void Tick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsClipboardHistoryWindow(hwnd))
            {
                return;
            }

            lock (_handledLock)
            {
                // 清理已经消失的窗口句柄：窗口被系统复用同一个句柄值时才不会漏处理
                _handledWindows.RemoveWhere(handle =>
                    !NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle));

                if (!_handledWindows.Add(hwnd))
                {
                    // 同一个窗口还没关掉：不再重复注入 ESC / 反复切换面板
                    return;
                }
            }

            DebugLog.Log($"检测到系统剪贴板历史窗口 (hwnd={hwnd}, pid={GetProcessId(hwnd)})，关闭并唤起 QuickClip");

            // 注入 ESC 是物理模拟按键，不会与窗口消息竞争；等待动作放到后台线程，避免阻塞计时器回调
            _ = Task.Run(() => Intercept(hwnd));
        }
        catch (Exception ex)
        {
            DebugLog.LogException("系统剪贴板历史守卫检查失败", ex);
        }
    }

    /// <summary>注入 ESC 关闭系统剪贴板历史窗口，确认关闭后再唤起面板。</summary>
    private void Intercept(IntPtr hwnd)
    {
        try
        {
            NativeMethods.SendEscape();

            var deadline = DateTime.UtcNow + EscapeCloseTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
                {
                    break;
                }

                Thread.Sleep(30);
            }

            bool closed = !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd);
            if (!closed)
            {
                // ESC 未能关闭（例如对高权限窗口被 UIPI 拦截）：仍然唤起面板，但不再重复注入
                DebugLog.Log($"ESC 未能关闭系统剪贴板历史窗口 (hwnd={hwnd})，已标记为处理过，不再重复注入");
            }

            _uiDispatcher.BeginInvoke(() => ToggleRequested?.Invoke());
        }
        catch (Exception ex)
        {
            DebugLog.LogException("关闭系统剪贴板历史窗口失败", ex);
        }
    }

    /// <summary>判断窗口是否为系统剪贴板历史窗口（类名 + 标题双重匹配，并排除本进程自己的窗口）。</summary>
    private static bool IsClipboardHistoryWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        var className = new StringBuilder(256);
        if (NativeMethods.GetClassName(hwnd, className, className.Capacity) == 0)
        {
            return false;
        }

        if (!string.Equals(className.ToString(), CoreWindowClassName, StringComparison.Ordinal))
        {
            return false;
        }

        // 排除自身窗口（QuickClip 面板不是 CoreWindow，这里只是防御性判断）
        if (GetProcessId(hwnd) == Environment.ProcessId)
        {
            return false;
        }

        string? title = GetWindowTitle(hwnd);
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (string keyword in TitleKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static uint GetProcessId(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
