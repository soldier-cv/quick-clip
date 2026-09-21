using System.Windows;
using System.Windows.Interop;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>基于 AddClipboardFormatListener 的剪贴板变更监听。</summary>
public sealed class ClipboardMonitor
{
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isListening;
    private CancellationTokenSource? _retryCts;

    /// <summary>当前是否已成功挂载系统剪贴板监听。</summary>
    public bool IsListening => _isListening;

    /// <summary>剪贴板内容变化时触发（UI 线程）。</summary>
    public event Action? ClipboardUpdated;

    /// <summary>将监听器挂载到指定窗口（需在窗口句柄创建后调用）。</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            DebugLog.Log("挂载剪贴板监听失败：窗口句柄为空");
            return;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        TryRegisterListener();
    }

    /// <summary>自愈重新挂载（例如 Explorer 重建后恢复剪贴板监听链）。</summary>
    public void Reattach(Window window)
    {
        DebugLog.Log("开始执行剪贴板监听器自愈重新挂载");
        Detach();
        Attach(window);
    }

    private void TryRegisterListener()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _retryCts?.Cancel();
        _retryCts = new CancellationTokenSource();
        var token = _retryCts.Token;

        bool ok = NativeMethods.AddClipboardFormatListener(_hwnd);
        if (ok)
        {
            _isListening = true;
            DebugLog.Log($"已成功注册系统剪贴板监听器 (hwnd={_hwnd})");
            return;
        }

        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        DebugLog.Log($"注册系统剪贴板监听器初次失败 (hwnd={_hwnd}, err={err})，启动退避重试");

        _ = Task.Run(async () =>
        {
            int[] delays = { 500, 1000, 2000, 4000, 6000 };
            foreach (int delay in delays)
            {
                try
                {
                    await Task.Delay(delay, token);
                    if (token.IsCancellationRequested || _hwnd == IntPtr.Zero)
                    {
                        return;
                    }

                    if (NativeMethods.AddClipboardFormatListener(_hwnd))
                    {
                        _isListening = true;
                        DebugLog.Log($"退避重试成功注册系统剪贴板监听器 (hwnd={_hwnd})");
                        return;
                    }

                    DebugLog.Log($"退避重试注册剪贴板监听未成功，等待下一次重试 (delay={delay}ms)");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    DebugLog.LogException("剪贴板监听重试异常", ex);
                }
            }

            DebugLog.Log("警告：已达到最大重试次数，剪贴板监听器仍未能注册成功");
        }, token);
    }

    public void Detach()
    {
        _retryCts?.Cancel();
        _retryCts = null;

        if (_hwnd != IntPtr.Zero)
        {
            if (_isListening)
            {
                NativeMethods.RemoveClipboardFormatListener(_hwnd);
                _isListening = false;
            }
            _hwnd = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardUpdated?.Invoke();
        }

        return IntPtr.Zero;
    }
}
