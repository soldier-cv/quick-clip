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

    public static MediaFontFamily Resolve(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName) ||
            string.Equals(storedName, DefaultDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultFamily;
        }

        try
        {
            // 为自定义字体自动拼接西文与中文字体回退链，避免无中文字形时触发昂贵的系统全局字形回退搜寻
            var family = new MediaFontFamily($"{storedName}, {FallbackChain}");
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
        app.Resources[FontFamilyKey] = family;
        foreach (System.Windows.Window window in app.Windows)
        {
            if (!Equals(window.FontFamily, family))
            {
                window.FontFamily = family;
            }
        }
    }
}
