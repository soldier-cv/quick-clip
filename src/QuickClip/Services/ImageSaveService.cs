using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 图片保存序号存储（由数据库实现）。序号只增不减，按「目录 + 日期」每天从 1 开始。
/// </summary>
public interface IImageSaveIndexStore
{
    Task<int> GetIndexAsync(string saveDir, DateOnly saveDate);
    Task SetIndexAsync(string saveDir, DateOnly saveDate, int index);
}

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
    /// 命名规则的「骨架」：把序号占位符替换成标记后的完整文件名（不含目录）。
    /// 同一天/同一前缀的保存共用一条计数记录，跨天会自动从 1 重新开始。
    /// </summary>
    internal static (string Prefix, string Suffix) BuildIndexSkeleton(
        string? pattern,
        ClipboardItem item,
        string? sourcePath,
        string ext)
    {
        string raw = string.IsNullOrWhiteSpace(pattern) ? DefaultFileNamePattern : pattern.Trim();
        ext = NormalizeExtension(ext);

        string skeleton = ExpandTokens(raw, 0, item, sourcePath, keepIndexSlot: true);
        skeleton = Sanitize(skeleton);

        int slot = skeleton.LastIndexOf(IndexMarker, StringComparison.Ordinal);
        if (slot < 0)
        {
            return (string.Empty, string.Empty);
        }

        string prefix = skeleton[..slot];
        string suffix = skeleton[(slot + IndexMarker.Length)..];

        // 骨架里若自带图片扩展名，去掉后再用作比对（扩展名单独作为键的一部分）
        string skeletonExt = Path.GetExtension(suffix);
        if (!string.IsNullOrEmpty(skeletonExt) && IsImageExtension(skeletonExt))
        {
            suffix = suffix[..^skeletonExt.Length];
        }

        return (prefix, suffix);
    }

    /// <summary>
    /// 扫描目录里符合骨架的文件，取最大序号（外部改名、换机、删库后的兜底修正）。
    /// </summary>
    internal static int ScanMaxIndex(string directory, string prefix, string suffix, string ext)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int max = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*" + ext))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length <= prefix.Length + suffix.Length)
                {
                    continue;
                }

                if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string middle = stem[prefix.Length..^suffix.Length];
                if (int.TryParse(middle, out int value) && value > max)
                {
                    max = value;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("扫描图片目录推断序号失败", ex);
        }

        return max;
    }

    /// <summary>序号槽位标记（不会出现在用户命名规则里）。</summary>
    private const string IndexMarker = "\u0002QC_IDX\u0002";

    /// <summary>把规则里的序号占位符整段换成标记，供骨架比对使用。</summary>
    private static string ReplaceNumberToken(string pattern, string marker)
    {
        string result = pattern;
        foreach (string token in new[] { "{index}", "{序号}", "{列表序号}" })
        {
            result = result.Replace(token, marker, StringComparison.OrdinalIgnoreCase);
        }

        return result.Replace("列表序号", marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// 给「另存为」对话框生成建议文件名：按目录现有文件推算下一个序号（只读，不写计数）。
    /// </summary>
    public static string BuildSuggestedFileName(
        string? pattern,
        ClipboardItem item,
        string? sourcePath,
        string directory)
    {
        string ext = NormalizeExtension(Path.GetExtension(sourcePath ?? string.Empty));
        var (prefix, suffix) = BuildIndexSkeleton(pattern, item, sourcePath, ext);

        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix))
        {
            // 规则不含序号
            return BuildFileName(pattern, 1, item, sourcePath, ext);
        }

        int next = ScanMaxIndex(directory, prefix, suffix, ext) + 1;
        return ComposeFileName(prefix, next, suffix, ext);
    }

    /// <summary>
    /// 生成文件名（含扩展名，不含目录）。
    /// </summary>
    /// <param name="pattern">命名规则；空则用默认规则。</param>
    /// <param name="index">序号（1 起，建议由 <see cref="ResolveNextIndex"/> 推算）。</param>
    /// <param name="item">条目（提供 id 等）。</param>
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
    /// <param name="template">命名规则。</param>
    /// <param name="index">序号（1 起）。</param>
    /// <param name="item">条目。</param>
    /// <param name="sourcePath">源图片路径。</param>
    /// <param name="keepIndexSlot">
    /// true 时序号位置保留 <see cref="IndexMarker"/> 占位，用于反查骨架；false 时填入实际序号。
    /// </param>
    internal static string ExpandTokens(
        string template,
        int index,
        ClipboardItem item,
        string? sourcePath,
        bool keepIndexSlot = false)
    {
        string work = template;

        // 先占住序号槽位，等日期与动态占位符都展开完再填数字，避免互相干扰
        work = ReplaceNumberToken(work, IndexMarker);

        work = ReplaceToken(work, "id", item.Id.ToString());
        work = ReplaceToken(work, "ext", Path.GetExtension(sourcePath ?? string.Empty).TrimStart('.').ToLowerInvariant());

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
        work = SnippetItem.ResolveText(work);

        if (!keepIndexSlot)
        {
            work = work.Replace(IndexMarker, Math.Max(index, 1).ToString(), StringComparison.Ordinal);
        }

        return work;
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

    /// <summary>替换文件名非法字符并裁剪长度，避免落盘失败。序号槽位标记原样保留。</summary>
    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            // 序号槽位标记是内部占位符，不能被当成控制字符替换掉
            if (string.CompareOrdinal(name, i, IndexMarker, 0, IndexMarker.Length) == 0)
            {
                sb.Append(IndexMarker);
                i += IndexMarker.Length - 1;
                continue;
            }

            char c = name[i];
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

    /// <summary>
    /// 带序号记忆的保存：先查序号表（O(1)），缺失或与实际文件冲突时才扫描目录兜底，
    /// 保存成功后把序号写回表，后续保存不再触碰文件系统枚举。
    /// </summary>
    /// <param name="sourcePath">源图片（预览缓存文件）。</param>
    /// <param name="directory">目标目录。</param>
    /// <param name="pattern">命名规则。</param>
    /// <param name="item">条目（提供 id）。</param>
    /// <param name="counters">序号存储；为 null 时退化为每次扫描目录。</param>
    /// <returns>实际落盘路径。</returns>
    public static async Task<string> SaveWithIndexAsync(
        string sourcePath,
        string directory,
        string? pattern,
        ClipboardItem item,
        IImageSaveIndexStore? counters,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        string ext = NormalizeExtension(Path.GetExtension(sourcePath));
        var (prefix, suffix) = BuildIndexSkeleton(pattern, item, sourcePath, ext);

        // 序号按「目录 + 日期」记账：每天从 1 重新开始，与命名规则解耦（改规则不会丢记忆）
        string saveDir = NormalizeDirKey(directory);
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        int last = counters == null ? 0 : await counters.GetIndexAsync(saveDir, today).ConfigureAwait(false);
        int candidate = last + 1;

        // 序号表缺失，或算出来的名字已被外部文件占用：扫描目录修正一次
        if (last <= 0 || File.Exists(Path.Combine(directory, ComposeFileName(prefix, candidate, suffix, ext))))
        {
            int scanned = ScanMaxIndex(directory, prefix, suffix, ext);
            if (scanned + 1 > candidate)
            {
                candidate = scanned + 1;
            }
        }

        string target = await SaveWithUniqueSuffixAsync(sourcePath, directory, prefix, suffix, ext, candidate, cancellationToken)
            .ConfigureAwait(false);

        // 用最终落盘的文件名反推实际序号，写回表（只增不减）
        if (counters != null)
        {
            int used = ExtractIndex(Path.GetFileNameWithoutExtension(target), prefix, suffix);
            if (used > 0)
            {
                await counters.SetIndexAsync(saveDir, today, used).ConfigureAwait(false);
            }
        }

        return target;
    }

    /// <summary>目录键：去尾部分隔符并统一小写，让同一目录的不同写法命中同一行。</summary>
    internal static string NormalizeDirKey(string directory)
    {
        string dir = string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim();
        try
        {
            dir = Path.GetFullPath(dir);
        }
        catch
        {
            // 非法路径按原样使用
        }

        return dir
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    private static string ComposeFileName(string prefix, int index, string suffix, string ext) =>
        Sanitize(prefix + index + suffix) + ext;

    /// <summary>从文件名里取出序号；格式不匹配返回 0。</summary>
    internal static int ExtractIndex(string stem, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(stem) ||
            stem.Length <= prefix.Length + suffix.Length ||
            !stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(stem[prefix.Length..^suffix.Length], out int value) ? value : 0;
    }

    /// <summary>以指定序号落盘；同名冲突时顺延序号，绝不覆盖。</summary>
    private static async Task<string> SaveWithUniqueSuffixAsync(
        string sourcePath,
        string directory,
        string prefix,
        string suffix,
        string ext,
        int startIndex,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);

        for (int i = startIndex; ; i++)
        {
            string target = Path.Combine(directory, ComposeFileName(prefix, i, suffix, ext));
            if (File.Exists(target))
            {
                continue;
            }

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
            catch (IOException)
            {
                // 检查与创建之间被占用：顺延到下一个序号重试
                TryDelete(target);
                source.Position = 0;
                if (i - startIndex > 1000)
                {
                    throw;
                }
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
            // 清理失败不影响顺延重试
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
