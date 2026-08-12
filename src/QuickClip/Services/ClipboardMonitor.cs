using System.Windows;
using System.Windows.Interop;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>基于 AddClipboardFormatListener 的剪贴板变更监听。</summary>
public sealed class ClipboardMonitor
{
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>剪贴板内容变化时触发（UI 线程）。</summary>
    public event Action? ClipboardUpdated;

    /// <summary>将监听器挂载到指定窗口（需在窗口句柄创建后调用）。</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    public void Detach()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
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
