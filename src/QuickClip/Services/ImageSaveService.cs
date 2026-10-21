using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 图片保存服务：默认目录、命名规则计算、落盘与在资源管理器中定位。
/// </summary>
/// <remarks>
/// 命名规则同时支持两类占位符：
/// · 常用短语动态占位符（<c>{date}</c>、<c>{timestamp}</c>、<c>{guid}</c> 等），由 <see cref="SnippetItem.ResolveText"/> 统一解析；
/// · 图片专属占位符（<c>{index}</c> / <c>{id}</c> / <c>{width}</c> / <c>{height}</c> / <c>{ext}</c>）；
/// · 以及裸写的日期段（<c>yyyyMMdd</c>、<c>HHmmss</c> 等），按 .NET 自定义格式展开。
///
/// 作者: xudong.hua, gemini
/// 时间: 2026-10-21 10:00 星期三
/// </remarks>
public static class ImageSaveService
{
    /// <summary>默认命名规则。</summary>
    public const string DefaultFileNamePattern = "QC_yyyyMMdd_{index}";

    /// <summary>可裸写的完整日期段（按长度倒序匹配）。只认整段常见格式，普通英文单词不会被误当成日期。</summary>
    private static readonly string[] DatePatterns = new[]
    {
        "yyyyMMdd_HHmmss_fff",
        "yyyy-MM-dd_HHmmss",
        "yyyy-MM-dd HHmmss",
        "yyyyMMdd_HHmmss",
        "yyyyMMdd-HHmmss",
        "yyyyMMddHHmmss",
        "yyyy-MM-dd_HHmm",
        "yyyyMMdd_HHmm",
        "yyyy-MM-dd",
        "HHmmss",
        "yyyyMMdd",
        "yyyyMM",
        "HHmm",
        "MMdd",
        "yyyy"
    }.OrderByDescending(p => p.Length).ToArray();

    /// <summary>文件名非法字符（含路径分隔符与控制字符）。</summary>
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>计算保存目录：配置为空或非绝对路径时回退默认目录。</summary>
    public static string ResolveDirectory(string? configured, AppPaths paths)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return paths.DefaultImageSaveDir;
        }

        string dir;
        try
        {
            dir = Environment.ExpandEnvironmentVariables(configured.Trim());
        }
        catch
        {
            return paths.DefaultImageSaveDir;
        }

        if (!Path.IsPathRooted(dir))
        {
            return paths.DefaultImageSaveDir;
        }

        // 预览缓存目录由程序自动清理，保存到这里会被当成孤儿文件删掉
        string previewRoot = Path.GetFullPath(paths.PreviewDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(dir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidate.Equals(previewRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(previewRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.Log("图片保存目录指向预览缓存目录，已回退默认目录");
            return paths.DefaultImageSaveDir;
        }

        return dir;
    }

    /// <summary>
    /// 按规则生成文件名（含扩展名，不含目录）。
    /// </summary>
    /// <param name="pattern">命名规则；空则用默认规则。</param>
    /// <param name="index">列表序号（1 起）。</param>
    /// <param name="item">条目（提供 id 与原始徽标）。</param>
    /// <param name="sourcePath">源图片路径（用于取尺寸与扩展名）。</param>
    /// <param name="ext">目标扩展名（含点）。</param>
    public static string BuildFileName(
        string? pattern,
        int index,
        ClipboardItem item,
        string? sourcePath,
        string ext)
    {
        string raw = string.IsNullOrWhiteSpace(pattern) ? DefaultFileNamePattern : pattern.Trim();
        ext = NormalizeExtension(ext);

        string name = ExpandTokens(raw, index, item, sourcePath);
        name = Sanitize(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"QC_{DateTime.Now:yyyyMMdd}_{Math.Max(index, 1)}";
        }

        // 生成名里自带图片扩展名时以源文件格式为准，避免 PNG 字节被写成 .jpg
        string currentExt = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(currentExt) && IsImageExtension(currentExt))
        {
            name = name[..^currentExt.Length];
        }

        return name + ext;
    }

    private static bool IsImageExtension(string ext) =>
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 展开模板：先解析花括号占位符，再把裸写日期段交给 .NET 自定义格式。
    /// </summary>
    internal static string ExpandTokens(string template, int index, ClipboardItem item, string? sourcePath)
    {
        string work = template;

        // 图片专属占位符先替换为哨兵，避免后序日期解析误伤
        work = ReplaceToken(work, "index", Math.Max(index, 1).ToString());
        work = ReplaceToken(work, "序号", Math.Max(index, 1).ToString());
        work = ReplaceToken(work, "列表序号", Math.Max(index, 1).ToString());
        work = ReplaceToken(work, "id", item.Id.ToString());
        work = ReplaceToken(work, "ext", Path.GetExtension(sourcePath ?? string.Empty).TrimStart('.').ToLowerInvariant());

        // 规则里直接写「列表序号」也按序号处理（与花括号写法等价）
        work = work.Replace("列表序号", Math.Max(index, 1).ToString(), StringComparison.Ordinal);

        if (work.Contains("{width}", StringComparison.OrdinalIgnoreCase) ||
            work.Contains("{height}", StringComparison.OrdinalIgnoreCase))
        {
            var (width, height) = TryGetPixelSize(sourcePath);
            work = ReplaceToken(work, "width", width.ToString());
            work = ReplaceToken(work, "height", height.ToString());
        }

        // 花括号日期段与裸写日期段（yyyyMMdd / HHmmss 等）先展开，
        // 再解析常用短语动态占位符，避免 GUID / 时间戳里恰好含日期字母被误伤。
        work = ExpandBracedDatePattern(work);
        work = ExpandBareDateTokens(work);

        // 常用短语动态占位符（{date}/{time}/{timestamp}/{user}/{guid} 等）
        return SnippetItem.ResolveText(work);
    }

    /// <summary>支持 {yyyyMMdd} 这类花括号日期段，展开后去掉花括号。</summary>
    private static string ExpandBracedDatePattern(string text)
    {
        return Regex.Replace(
            text,
            @"\{([yMdHmsfFtTzK]+)\}",
            match =>
            {
                string pattern = match.Groups[1].Value;
                try
                {
                    // 单字符格式串会被当成标准格式（如 "d" 变成短日期），改用 % 前缀按自定义格式解析
                    string format = pattern.Length == 1 ? "%" + pattern : pattern;
                    return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    return match.Value;
                }
            });
    }

    private static string ReplaceToken(string input, string token, string value)
    {
        return input.Replace("{" + token + "}", value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 展开裸写的日期段（如 yyyyMMdd）。只匹配完整的常见日期格式，
    /// 因此 "QC_" / "Screenshot" 这类普通文字不会被误识别。
    /// </summary>
    internal static string ExpandBareDateTokens(string text, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        DateTime time = now ?? DateTime.Now;
        var sb = new StringBuilder(text.Length + 8);
        int i = 0;

        while (i < text.Length)
        {
            string? pattern = MatchDatePattern(text, i);
            if (pattern == null)
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            try
            {
                sb.Append(time.ToString(pattern, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                sb.Append(pattern);
            }

            i += pattern.Length;
        }

        return sb.ToString();
    }

    /// <summary>在指定位置尝试匹配一个完整日期格式。</summary>
    private static string? MatchDatePattern(string text, int start)
    {
        foreach (string pattern in DatePatterns)
        {
            if (start + pattern.Length > text.Length)
            {
                continue;
            }

            if (string.CompareOrdinal(text, start, pattern, 0, pattern.Length) == 0)
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>Windows 保留设备名，直接用作文件名会创建失败。</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>替换文件名非法字符并裁剪长度，避免落盘失败。</summary>
    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsControl(c) || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        string result = sb.ToString().Trim().TrimEnd('.');
        const int maxLength = 120;
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd();
        }

        if (ReservedNames.Contains(result, StringComparer.OrdinalIgnoreCase))
        {
            result = "_" + result;
        }

        return result;
    }

    internal static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return ".png";
        }

        string value = ext.Trim();
        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        return value.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp"
            ? value.ToLowerInvariant()
            : ".png";
    }

    private static (int Width, int Height) TryGetPixelSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return (0, 0);
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>在同名文件存在时追加 _2、_3…，绝不覆盖用户已有文件。</summary>
    public static string EnsureUniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem}_{DateTime.Now:HHmmssfff}{ext}");
    }

    /// <summary>
    /// 保存图片：目录不存在时自动创建，同名自动避让。
    /// </summary>
    /// <returns>实际落盘路径。</returns>
    public static async Task<string> SaveAsync(
        string sourcePath,
        string directory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);

        for (int attempt = 0; ; attempt++)
        {
            string target = EnsureUniquePath(directory, fileName);
            try
            {
                await using var dest = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);
                await source.CopyToAsync(dest, cancellationToken);
                return target;
            }
            catch (IOException) when (attempt < 2)
            {
                // 文件名在检查与创建之间被占用：清掉半成品后换个序号重试
                TryDelete(target);
                source.Position = 0;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响外层重试
        }
    }

    /// <summary>在资源管理器中选中该文件；文件已不存在返回 false。</summary>
    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("定位图片文件失败", ex);
            return false;
        }
    }
}
