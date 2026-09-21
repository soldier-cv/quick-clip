using System.Windows;
using System.Windows.Markup;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace QuickClip.Services;

/// <summary>
/// 界面字体：只使用本机已安装字体，不内置字体文件。
/// 空值表示系统默认（Segoe UI / Microsoft YaHei UI 回退链）。
/// </summary>
public static class AppFontService
{
    public const string FontFamilyKey = "Theme.FontFamily";
    public const string DefaultDisplayName = "系统默认";
    public const string FallbackChain = "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, sans-serif";

    public static readonly MediaFontFamily DefaultFamily = new(FallbackChain);

    private static IReadOnlyList<string>? _cachedInstalledFamilies;
    private static readonly object _lock = new();

    public static IReadOnlyList<string> ListInstalledFamilies()
    {
        if (_cachedInstalledFamilies != null)
        {
            return _cachedInstalledFamilies;
        }

        lock (_lock)
        {
            if (_cachedInstalledFamilies != null)
            {
                return _cachedInstalledFamilies;
            }

            var names = System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f =>
                {
                    if (f.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("zh-cn"), out string? zh) &&
                        !string.IsNullOrWhiteSpace(zh))
                    {
                        return zh;
                    }

                    return f.Source;
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            names.Insert(0, DefaultDisplayName);
            _cachedInstalledFamilies = names;
            return _cachedInstalledFamilies;
        }
    }

    public static string NormalizeStoredName(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return string.Empty;
        }

        string trimmed = storedName.Trim();
        return string.Equals(trimmed, DefaultDisplayName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    public static MediaFontFamily Resolve(string? storedName)
    {
        string name = NormalizeStoredName(storedName);
        if (string.IsNullOrEmpty(name))
        {
            return DefaultFamily;
        }

        try
        {
            // 为自定义字体自动拼接西文与中文字体回退链，避免无中文字形时触发昂贵的系统全局字形回退搜寻
            var family = new MediaFontFamily($"{name}, {FallbackChain}");
            if (family.FamilyNames.Count > 0 || family.Source.Length > 0)
            {
                return family;
            }
        }
        catch
        {
            // 字体已卸载则回退
        }

        return DefaultFamily;
    }

    public static void Apply(string? storedName)
    {
        var app = System.Windows.Application.Current;
        if (app?.Resources == null)
        {
            return;
        }

        var family = Resolve(storedName);
        if (app.Resources[FontFamilyKey] is MediaFontFamily current &&
            string.Equals(current.Source, family.Source, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        app.Resources[FontFamilyKey] = family;
    }

    /// <summary>
    /// 仅在指定窗口覆盖 Theme.FontFamily，不改应用级资源。
    /// 下拉浏览时避免主窗口历史列表跟着整树换字体。
    /// </summary>
    public static void PreviewOn(Window window, string? storedName)
    {
        window.Resources[FontFamilyKey] = Resolve(storedName);
    }

    public static void ClearPreview(Window window)
    {
        if (window.Resources.Contains(FontFamilyKey))
        {
            window.Resources.Remove(FontFamilyKey);
        }
    }
}
