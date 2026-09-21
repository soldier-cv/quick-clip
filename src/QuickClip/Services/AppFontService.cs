using System.IO;
using System.Windows;
using System.Windows.Markup;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace QuickClip.Services;

/// <summary>
/// 界面字体服务：支持系统默认、内置 JetBrains Mono、Cascadia Mono，
/// 以及通过文件路径加载自定义系统/外部字体文件。
/// </summary>
public static class AppFontService
{
    public const string FontFamilyKey = "Theme.FontFamily";

    // 存储标识
    public const string KeyDefault = "";
    public const string KeyJetBrainsMono = "JetBrains Mono";
    public const string KeyCascadiaMono = "Cascadia Mono";

    // 精炼展示名（无多余修饰）
    public const string DefaultDisplayName = "系统默认";
    public const string JetBrainsMonoDisplayName = "JetBrains Mono";
    public const string CascadiaMonoDisplayName = "Cascadia Mono";

    public const string FallbackChain = "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, sans-serif";
    public const string JetBrainsMonoPackPath = "pack://application:,,,/QuickClip;component/Assets/Fonts/#JetBrains Mono";

    public static readonly MediaFontFamily DefaultFamily = new(FallbackChain);

    public static IReadOnlyList<string> GetPresetNames() => new[]
    {
        DefaultDisplayName,
        JetBrainsMonoDisplayName,
        CascadiaMonoDisplayName
    };

    public static bool IsFontFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string ext = Path.GetExtension(path);
            return (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)) &&
                   File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static string GetFontFamilyNameFromFile(string filePath)
    {
        try
        {
            var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(filePath), "./");
            foreach (var family in families)
            {
                if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("zh-cn"), out string? zh) &&
                    !string.IsNullOrWhiteSpace(zh))
                {
                    return zh;
                }

                if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string? en) &&
                    !string.IsNullOrWhiteSpace(en))
                {
                    return en;
                }

                if (!string.IsNullOrWhiteSpace(family.Source))
                {
                    string source = family.Source;
                    int hashIdx = source.IndexOf('#');
                    return hashIdx >= 0 ? source[(hashIdx + 1)..] : Path.GetFileNameWithoutExtension(filePath);
                }
            }
        }
        catch
        {
            // 解析失败退化为文件名
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    public static string NormalizeStoredName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string trimmed = input.Trim();
        if (string.Equals(trimmed, DefaultDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (string.Equals(trimmed, JetBrainsMonoDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return KeyJetBrainsMono;
        }

        if (string.Equals(trimmed, CascadiaMonoDisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Cascadia Code", StringComparison.OrdinalIgnoreCase))
        {
            return KeyCascadiaMono;
        }

        // 文件路径保持原样
        return trimmed;
    }

    public static string ToDisplayName(string? storedName)
    {
        string normalized = NormalizeStoredName(storedName);
        if (string.IsNullOrEmpty(normalized))
        {
            return DefaultDisplayName;
        }

        if (string.Equals(normalized, KeyJetBrainsMono, StringComparison.OrdinalIgnoreCase))
        {
            return JetBrainsMonoDisplayName;
        }

        if (string.Equals(normalized, KeyCascadiaMono, StringComparison.OrdinalIgnoreCase))
        {
            return CascadiaMonoDisplayName;
        }

        if (IsFontFilePath(normalized))
        {
            string familyName = GetFontFamilyNameFromFile(normalized);
            return $"自定义：{familyName}";
        }

        return normalized;
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
            // 1. 自定义外部字体文件路径
            if (IsFontFilePath(name))
            {
                string familyName = GetFontFamilyNameFromFile(name);
                var fileUri = new Uri(name);
                return new MediaFontFamily(fileUri, $"./#{familyName}, {FallbackChain}");
            }

            // 2. 内置 JetBrains Mono
            if (string.Equals(name, KeyJetBrainsMono, StringComparison.OrdinalIgnoreCase))
            {
                string localFontPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts", "JetBrainsMono-Regular.ttf");
                if (File.Exists(localFontPath))
                {
                    var folderUri = new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts") + "\\");
                    return new MediaFontFamily(folderUri, $"./#JetBrains Mono, {FallbackChain}");
                }

                var packUri = new Uri("pack://application:,,,/Assets/Fonts/");
                return new MediaFontFamily(packUri, $"./#JetBrains Mono, {FallbackChain}");
            }

            // 3. Cascadia Mono
            if (string.Equals(name, KeyCascadiaMono, StringComparison.OrdinalIgnoreCase))
            {
                return new MediaFontFamily($"Cascadia Mono, Cascadia Code, {FallbackChain}");
            }

            // 4. 其他常规系统字体
            var family = new MediaFontFamily($"{name}, {FallbackChain}");
            if (family.FamilyNames.Count > 0 || family.Source.Length > 0)
            {
                return family;
            }
        }
        catch
        {
            // 字体异常则回退
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
