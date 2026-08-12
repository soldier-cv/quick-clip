using System.Windows;
using System.Windows.Media;
using QuickClip.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QuickClip.Services;

/// <summary>
/// 应用主题：写入 Application 资源字典，供 XAML DynamicResource 绑定；
/// 并统一主窗/设置窗不透明底色与 DWM 关闭逻辑。
/// </summary>
public static class ThemeService
{
    public const string PanelBrushKey = "Theme.Panel";
    public const string CardBrushKey = "Theme.Card";
    public const string CardHoverBrushKey = "Theme.CardHover";
    public const string CardSelectedBrushKey = "Theme.CardSelected";
    public const string BorderBrushKey = "Theme.Border";
    public const string BorderStrongBrushKey = "Theme.BorderStrong";
    public const string TextBrushKey = "Theme.Text";
    public const string TextSecondaryBrushKey = "Theme.TextSecondary";
    public const string TextMutedBrushKey = "Theme.TextMuted";
    public const string AccentBrushKey = "Theme.Accent";
    public const string AccentMutedBrushKey = "Theme.AccentMuted";
    public const string PinBrushKey = "Theme.Pin";
    public const string SearchBrushKey = "Theme.Search";
    public const string BadgeBrushKey = "Theme.Badge";

    /// <summary>当前生效主题。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Terminal;

    public static ThemePalette CurrentPalette => ThemePalette.Get(Current);

    /// <summary>主题切换后触发（UI 可刷新非 DynamicResource 控件）。</summary>
    public static event Action? Changed;

    /// <summary>应用主题到全局资源，并可选同步 WPF-UI 明暗。</summary>
    public static void Apply(AppTheme theme)
    {
        // 已废弃的 Dracula 等非法值回退 Terminal
        if (!Enum.IsDefined(theme))
        {
            theme = AppTheme.Terminal;
        }

        Current = theme;
        ThemePalette p = ThemePalette.Get(theme);
        var uiTheme = p.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        // 先套明暗，再强制强调色 = 主题 Accent。
        // updateAccent:true 会吃系统蓝，导致 Terminal 既有绿又有蓝。
        // 注意：Apply 会重载 WPF-UI 主题字典，可能冲掉我们的滚动条样式，后面要再挂回。
        ApplicationThemeManager.Apply(uiTheme, WindowBackdropType.None, updateAccent: false);
        try
        {
            ApplicationAccentColorManager.Apply(p.Accent, uiTheme, false, false);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("应用主题强调色失败（可忽略）", ex);
        }

        var app = System.Windows.Application.Current;
        if (app?.Resources == null)
        {
            return;
        }

        EnsureThemeScrollBarDictionary(app.Resources);

        SetBrush(app.Resources, PanelBrushKey, p.Panel);
        SetBrush(app.Resources, CardBrushKey, p.Card);
        SetBrush(app.Resources, CardHoverBrushKey, p.CardHover);
        SetBrush(app.Resources, CardSelectedBrushKey, p.CardSelected);
        SetBrush(app.Resources, BorderBrushKey, p.Border);
        SetBrush(app.Resources, BorderStrongBrushKey, p.BorderStrong);
        SetBrush(app.Resources, TextBrushKey, p.Text);
        SetBrush(app.Resources, TextSecondaryBrushKey, p.TextSecondary);
        SetBrush(app.Resources, TextMutedBrushKey, p.TextMuted);
        SetBrush(app.Resources, AccentBrushKey, p.Accent);
        SetBrush(app.Resources, AccentMutedBrushKey, p.AccentMuted);
        SetBrush(app.Resources, PinBrushKey, p.Pin);
        SetBrush(app.Resources, SearchBrushKey, p.Search);
        SetBrush(app.Resources, BadgeBrushKey, p.Badge);

        // 已打开窗口立即刷底色（主窗 Fluent + 设置普通 Window）
        foreach (Window window in app.Windows)
        {
            var root = FindRootPanel(window);
            if (window is FluentWindow fluent)
            {
                WindowChromeHelper.Apply(fluent, root, p);
            }
            else
            {
                WindowChromeHelper.Apply(window, root, p);
            }
        }

        DebugLog.Log($"主题已应用: {p.DisplayName}");
        Changed?.Invoke();
    }

    /// <summary>确保资源键存在（启动时在主题应用前也可安全绑定）。</summary>
    public static void EnsureDefaultResources()
    {
        Apply(Current);
    }

    /// <summary>
    /// ApplicationThemeManager.Apply 会重载主题字典，可能挤掉 ThemeScrollBar。
    /// 每次主题切换后确保覆盖式滚动条字典仍在 MergedDictionaries 末尾。
    /// </summary>
    private static void EnsureThemeScrollBarDictionary(ResourceDictionary appResources)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Themes/ThemeScrollBar.xaml", UriKind.Absolute);
            // 先移除旧实例，避免重复
            for (int i = appResources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var d = appResources.MergedDictionaries[i];
                if (d.Source != null &&
                    d.Source.OriginalString.Contains("ThemeScrollBar", StringComparison.OrdinalIgnoreCase))
                {
                    appResources.MergedDictionaries.RemoveAt(i);
                }
            }

            appResources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
        catch (Exception ex)
        {
            DebugLog.LogException("重新挂载 ThemeScrollBar 失败", ex);
        }
    }

    private static System.Windows.Controls.Panel? FindRootPanel(Window window)
    {
        if (window.Content is System.Windows.Controls.Panel panel)
        {
            return panel;
        }

        if (window.Content is FrameworkElement fe)
        {
            if (fe.FindName("RootGrid") is System.Windows.Controls.Panel named)
            {
                return named;
            }

            if (fe is System.Windows.Controls.Border { Child: System.Windows.Controls.Panel childPanel })
            {
                return childPanel;
            }

            if (fe is System.Windows.Controls.Border { Child: FrameworkElement childFe } &&
                childFe.FindName("RootGrid") is System.Windows.Controls.Panel nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetBrush(ResourceDictionary resources, string key, System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
