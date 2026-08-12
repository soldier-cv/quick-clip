using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace QuickClip.Services;

/// <summary>
/// 主面板与设置窗口统一底色：关闭 DWM 系统材质，铺主题 Panel 色，消除白边/透色。
/// 支持 FluentWindow 与普通 Window（设置页用自绘铬）。
/// </summary>
public static class WindowChromeHelper
{
    public static SolidColorBrush CreateSolidBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>兼容旧调用：使用当前主题 Panel 色。</summary>
    public static SolidColorBrush CreateSolidDarkBrush() =>
        CreateSolidBrush(ThemeService.CurrentPalette.Panel);

    /// <summary>为 FluentWindow 应用当前（或指定）主题底色。</summary>
    public static void Apply(FluentWindow window, System.Windows.Controls.Panel? root = null, ThemePalette? palette = null)
    {
        ThemePalette p = palette ?? ThemeService.CurrentPalette;
        window.WindowBackdropType = WindowBackdropType.None;
        ApplyCore(window, root, p);
    }

    /// <summary>为普通 Window（设置页）应用主题底色与 DWM 关闭。</summary>
    public static void Apply(Window window, System.Windows.Controls.Panel? root = null, ThemePalette? palette = null)
    {
        ApplyCore(window, root, palette ?? ThemeService.CurrentPalette);
    }

    private static void ApplyCore(Window window, System.Windows.Controls.Panel? root, ThemePalette p)
    {
        var solid = CreateSolidBrush(p.Panel);
        var border = CreateSolidBrush(p.Border);

        window.Background = solid;
        window.BorderBrush = border;
        window.BorderThickness = new Thickness(1);
        window.Padding = new Thickness(0);

        if (root != null)
        {
            root.Background = solid;
        }
        else if (window.Content is System.Windows.Controls.Panel panel)
        {
            panel.Background = solid;
        }

        if (window.IsLoaded)
        {
            TryDisableSystemBackdrop(window);
        }
        else
        {
            window.SourceInitialized -= OnSourceInitializedDisableBackdrop;
            window.SourceInitialized += OnSourceInitializedDisableBackdrop;
            window.Loaded -= OnLoadedReapply;
            window.Loaded += OnLoadedReapply;
        }
    }

    private static void OnSourceInitializedDisableBackdrop(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            TryDisableSystemBackdrop(w);
        }
    }

    private static void OnLoadedReapply(object? sender, RoutedEventArgs e)
    {
        if (sender is not Window w)
        {
            return;
        }

        System.Windows.Controls.Panel? root = w.Content as System.Windows.Controls.Panel
            ?? (w.Content as FrameworkElement)?.FindName("RootGrid") as System.Windows.Controls.Panel
            ?? ((w.Content as System.Windows.Controls.Border)?.Child as FrameworkElement)?.FindName("RootGrid") as System.Windows.Controls.Panel;

        if (w is FluentWindow fluent)
        {
            Apply(fluent, root);
        }
        else
        {
            Apply(w, root);
        }
    }

    private static void TryDisableSystemBackdrop(Window window)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
            const int DWMSBT_NONE = 1;
            int value = DWMSBT_NONE;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));

            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            int dark = ThemeService.CurrentPalette.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // 不画系统非客户区边框色（避免白/浅描边）
            const int DWMWA_BORDER_COLOR = 34;
            int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

            const int DWMWA_CAPTION_COLOR = 35;
            var panel = ThemeService.CurrentPalette.Panel;
            int caption = panel.R | (panel.G << 8) | (panel.B << 16);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        catch
        {
            // 旧系统无此属性时忽略
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
