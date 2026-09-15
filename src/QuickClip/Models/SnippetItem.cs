namespace QuickClip.Models;

/// <summary>常用短语/模版条目。</summary>
public sealed class SnippetItem
{
    public long Id { get; set; }

    /// <summary>分类名称（如：通用、代码、办公、回复）。</summary>
    public string Category { get; set; } = "通用";

    /// <summary>短语标题或备注。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>短语正文内容，支持 {date}、{time}、{clipboard} 占位符。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>内容类型（text / html / image）。默认为 text。</summary>
    public string ContentType { get; set; } = "text";

    /// <summary>排序序号。</summary>
    public int SortOrder { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>计算替换动态占位符后的真实内容。</summary>
    public string ResolveContent(string? currentClipboard = null)
    {
        if (string.IsNullOrEmpty(Content))
        {
            return string.Empty;
        }

        string result = Content
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH:mm:ss"))
            .Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        if (result.Contains("{clipboard}"))
        {
            result = result.Replace("{clipboard}", currentClipboard ?? string.Empty);
        }

        return result;
    }
}
