using System.Collections.Concurrent;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>内存搜索：关键词模糊匹配 + 拼音首字母匹配。</summary>
public static class SearchService
{
    // 文本 -> 拼音首字母 缓存，避免重复计算（有上限，避免长时间运行后无限增长）
    private static readonly ConcurrentDictionary<string, string> InitialsCache = new();

    /// <summary>缓存条目上限（键是剪贴板正文，必须设上限）。</summary>
    private const int MaxCacheEntries = 512;

    /// <summary>超过该长度的文本不做拼音首字母（逐字查表代价高且几乎用不上）。</summary>
    private const int MaxPinyinTextLength = 4096;

    public static bool IsMatch(ClipboardItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string q = query.Trim().ToLowerInvariant();
        string text = item.TextContent ?? string.Empty;
        string lower = text.ToLowerInvariant();

        if (lower.Contains(q, StringComparison.Ordinal))
        {
            return true;
        }

        if (item.QrContent is { Length: > 0 } qr &&
            qr.ToLowerInvariant().Contains(q, StringComparison.Ordinal))
        {
            return true;
        }

        // 拼音首字母：sjjg 匹配「设计架构」。单字符不走这条，避免 a 命中 applesj 这类首字母串。
        if (q.Length >= 2)
        {
            string textInitials = HanInitials(lower);
            if (textInitials.Contains(q, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetInitialsCached(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxPinyinTextLength)
        {
            return string.Empty;
        }

        if (InitialsCache.TryGetValue(text, out string? cached))
        {
            return cached;
        }

        string initials = PinyinUtil.GetInitials(text);
        if (InitialsCache.Count >= MaxCacheEntries)
        {
            InitialsCache.Clear();
        }

        InitialsCache.TryAdd(text, initials);
        return initials;
    }

    /// <summary>只保留汉字的拼音首字母。拉丁字符留给上面的字面匹配，不再混进首字母串。</summary>
    private static string HanInitials(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                sb.Append(ch);
            }
        }

        return sb.Length == 0 ? string.Empty : GetInitialsCached(sb.ToString());
    }
}
