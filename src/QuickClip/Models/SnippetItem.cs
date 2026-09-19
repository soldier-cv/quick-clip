using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickClip.Models;

/// <summary>常用短语/模版条目。</summary>
public sealed class SnippetItem
{
    public long Id { get; set; }

    /// <summary>分类名称（如：通用、代码、办公、回复）。</summary>
    public string Category { get; set; } = "通用";

    /// <summary>短语标题或备注。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>短语正文内容，支持动态占位符（如 {date}、{time}、{guid}、{clipboard} 等）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>内容类型（text / html / image）。默认为 text。</summary>
    public string ContentType { get; set; } = "text";

    /// <summary>排序序号。</summary>
    public int SortOrder { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>计算替换动态占位符后的真实内容（不区分占位符大小写）。</summary>
    public string ResolveContent(string? currentClipboard = null)
    {
        if (string.IsNullOrEmpty(Content))
        {
            return string.Empty;
        }

        DateTime now = DateTime.Now;
        CultureInfo zhCulture = new("zh-CN");

        string result = Content;

        // 基础日期时间
        result = ReplaceIgnoreCase(result, "{date}", now.ToString("yyyy-MM-dd"));
        result = ReplaceIgnoreCase(result, "{time}", now.ToString("HH:mm:ss"));
        result = ReplaceIgnoreCase(result, "{datetime}", now.ToString("yyyy-MM-dd HH:mm:ss"));
        result = ReplaceIgnoreCase(result, "{year}", now.ToString("yyyy"));
        result = ReplaceIgnoreCase(result, "{month}", now.ToString("MM"));
        result = ReplaceIgnoreCase(result, "{day}", now.ToString("dd"));
        result = ReplaceIgnoreCase(result, "{weekday}", now.ToString("dddd", zhCulture));
        result = ReplaceIgnoreCase(result, "{week}", now.ToString("ddd", zhCulture));

        // 时间戳
        result = ReplaceIgnoreCase(result, "{timestamp}", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
        result = ReplaceIgnoreCase(result, "{timestamp_ms}", DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString());

        // 用户与设备信息
        result = ReplaceIgnoreCase(result, "{username}", Environment.UserName);
        result = ReplaceIgnoreCase(result, "{user}", Environment.UserName);
        result = ReplaceIgnoreCase(result, "{computername}", Environment.MachineName);
        result = ReplaceIgnoreCase(result, "{device}", Environment.MachineName);

        // 剪贴板文本
        if (Regex.IsMatch(result, @"\{clipboard\}", RegexOptions.IgnoreCase))
        {
            result = ReplaceIgnoreCase(result, "{clipboard}", currentClipboard ?? string.Empty);
        }

        // 唯一标识符（每次命中生成全新 GUID）
        result = Regex.Replace(result, @"\{(guid|uuid)\}", _ => Guid.NewGuid().ToString("D"), RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{(guid_upper|uuid_upper)\}", _ => Guid.NewGuid().ToString("D").ToUpperInvariant(), RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{(guid_n|uuid_n|uuid_simple)\}", _ => Guid.NewGuid().ToString("N"), RegexOptions.IgnoreCase);

        // 随机数字串
        result = Regex.Replace(result, @"\{random4\}", _ => Random.Shared.Next(1000, 10000).ToString(), RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{random6\}", _ => Random.Shared.Next(100000, 1000000).ToString(), RegexOptions.IgnoreCase);

        return result;
    }

    private static string ReplaceIgnoreCase(string input, string pattern, string replacement)
    {
        return Regex.Replace(input, Regex.Escape(pattern), replacement, RegexOptions.IgnoreCase);
    }
}

