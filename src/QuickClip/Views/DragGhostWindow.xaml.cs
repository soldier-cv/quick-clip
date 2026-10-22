using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QuickClip.Native;
using QuickClip.Services;
using QuickClip.ViewModels;

namespace QuickClip.Views;

/// <summary>
/// 拖出时跟着指针的内容预览。独立窗口，不被主面板裁切，也不参与命中。
/// </summary>
public partial class DragGhostWindow : Window
{
    public DragGhostWindow()
    {
        InitializeComponent();
    }

    public void ShowContent(ClipboardItemViewModel vm)
    {
        if (vm.IsImage && vm.Thumbnail is BitmapImage image)
        {
            GhostImage.Source = image;
            GhostImage.Visibility = Visibility.Visible;
            GhostText.Visibility = Visibility.Collapsed;
        }
        else
        {
            GhostText.Text = vm.DragGhostText;
            GhostText.Visibility = Visibility.Visible;
            GhostImage.Visibility = Visibility.Collapsed;
        }

        if (!IsVisible)
        {
            Show();
            MakeClickThrough();
        }

        UpdateLayout();
    }

    /// <summary>鼠标穿透，避免预览窗自己挡住放下目标。</summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int ex = NativeMethods.GetWindowLong(hwnd, PasteTargetFilter.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            hwnd,
            PasteTargetFilter.GWL_EXSTYLE,
            ex | PasteTargetFilter.WS_EX_TRANSPARENT | PasteTargetFilter.WS_EX_LAYERED | PasteTargetFilter.WS_EX_NOACTIVATE);
    }

    /// <summary>拖动期间界面不刷新，用系统接口直接挪位置。</summary>
    public void MoveToCursor()
    {
        if (!IsVisible || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            cursor.X + 16,
            cursor.Y + 16,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
