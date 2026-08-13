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

        window.SizeChanged -= OnWindowSizeChangedRound;
        window.SizeChanged += OnWindowSizeChangedRound;

        if (window.IsLoaded)
        {
            TryDisableSystemBackdrop(window);
            ApplyWin10RoundRegion(window);
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

            // Win11：系统圆角。Win10 无此属性，由 ApplyWin10RoundRegion 裁 HWND。
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            int corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // 旧系统无此属性时忽略
        }
    }

    /// <summary>Win11 系统圆角半径（DIP），Win10 区域裁剪与此对齐。</summary>
    public const double WindowCornerRadiusDip = 8;

    private static void OnWindowSizeChangedRound(object sender, SizeChangedEventArgs e)
    {
        if (sender is Window w)
        {
            ApplyWin10RoundRegion(w);
        }
    }

    /// <summary>
    /// Win10 没有 DWM 圆角，用 SetWindowRgn 把主列表/设置窗裁成圆角。
    /// Win11 交给系统，避免双重裁剪。
    /// </summary>
    public static void ApplyWin10RoundRegion(Window window)
    {
        if (SupportsDwmRoundedCorners())
        {
            return;
        }

        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || window.ActualWidth < 2 || window.ActualHeight < 2)
            {
                return;
            }

            GetDpiScale(window, out double dpiX, out double dpiY);
            int width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpiX));
            int height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpiY));
            int ellipse = Math.Max(2, (int)Math.Round(WindowCornerRadiusDip * 2 * ((dpiX + dpiY) / 2)));

            // GDI 区域右/下为开区间，+1 才能盖住最后一列像素
            IntPtr rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, ellipse, ellipse);
            if (rgn == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(hwnd, rgn, true) == 0)
            {
                DeleteObject(rgn);
            }
        }
        catch
        {
            // 远程桌面 / 旧构建无 GDI 圆角时忽略
        }
    }

    private static bool SupportsDwmRoundedCorners()
    {
        try
        {
            Version v = Environment.OSVersion.Version;
            return v.Major >= 10 && v.Build >= 22000;
        }
        catch
        {
            return false;
        }
    }

    private static void GetDpiScale(Window window, out double dpiX, out double dpiY)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
            if (dpiX > 0 && dpiY > 0)
            {
                return;
            }
        }

        dpiX = 1;
        dpiY = 1;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
