using System.Collections.Concurrent;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>内存搜索：关键词模糊匹配 + 拼音首字母匹配。</summary>
public static class SearchService
{
    // 文本 -> 拼音首字母 缓存，避免重复计算
    private static readonly ConcurrentDictionary<string, string> InitialsCache = new();

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

        // 拼音首字母匹配：如 "sjjg" 匹配 "设计架构"
        string queryInitials = GetInitialsCached(q);
        if (queryInitials.Length > 0)
        {
            string textInitials = GetInitialsCached(lower);
            if (textInitials.Contains(queryInitials, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetInitialsCached(string text)
    {
        return InitialsCache.GetOrAdd(text, static key => PinyinUtil.GetInitials(key));
    }
}
