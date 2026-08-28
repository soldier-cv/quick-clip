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
/// </summary>
public sealed class SystemClipboardGuard : IDisposable
{
    /// <summary>系统剪贴板历史窗口的窗口类名（XAML CoreWindow）。</summary>
    private const string CoreWindowClassName = "Windows.UI.Core.CoreWindow";

    /// <summary>系统剪贴板历史窗口标题关键字（中文 / 英文，不区分大小写）。</summary>
    private static readonly string[] TitleKeywords = { "剪贴板历史", "剪贴板历史记录", "Clipboard history" };

    /// <summary>轮询间隔：兼顾响应速度与 CPU 开销（约 8 次/秒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>同一窗口句柄再次处理前的等待时间，避免窗口句柄值被系统复用后重复触发。</summary>
    private static readonly TimeSpan SameWindowCooldown = TimeSpan.FromSeconds(5);

    /// <summary>注入 ESC 后等待其被系统剪贴板历史窗口消费的缓冲，避免 ESC 误落到刚唤起的面板上。</summary>
    private static readonly TimeSpan EscapeSettleDelay = TimeSpan.FromMilliseconds(90);

    private readonly Dispatcher _uiDispatcher;
    private readonly System.Threading.Timer _timer;

    private IntPtr _lastIntercepted = IntPtr.Zero;
    private DateTime _lastInterceptUtc = DateTime.MinValue;
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

            // 防抖：同一窗口句柄在冷却期内只处理一次（窗口关闭后新弹出的窗口是新句柄，会再次处理）
            bool sameWindow = hwnd == _lastIntercepted;
            if (sameWindow && DateTime.UtcNow - _lastInterceptUtc < SameWindowCooldown)
            {
                return;
            }

            _lastIntercepted = hwnd;
            _lastInterceptUtc = DateTime.UtcNow;
            DebugLog.Log($"检测到系统剪贴板历史窗口 (hwnd={hwnd})，关闭并唤起 QuickClip");

            // 注入 ESC 关闭系统剪贴板历史：ESC 是其系统设计的关闭键，SendInput 是物理模拟按键，
            // 不受 UIPI 权限隔离限制，对高权限窗口同样有效，且不会与窗口消息产生竞争。
            NativeMethods.SendEscape();

            // 等 ESC 被剪贴板历史窗口消费后再唤起面板，防止 ESC 误触发刚获得焦点的面板
            Thread.Sleep(EscapeSettleDelay);
            _uiDispatcher.BeginInvoke(() => ToggleRequested?.Invoke());
        }
        catch (Exception ex)
        {
            DebugLog.LogException("系统剪贴板历史守卫检查失败", ex);
        }
    }

    /// <summary>判断窗口是否为系统剪贴板历史窗口（类名 + 标题双重匹配，避免误伤普通 UWP 窗口）。</summary>
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