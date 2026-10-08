namespace QuickClip.Models;

/// <summary>剪贴板条目内容类型。</summary>
public enum ClipboardContentType
{
    Text,
    Link,
    Image,
    File
}

/// <summary>剪贴板历史条目。</summary>
public sealed class ClipboardItem
{
    public long Id { get; set; }

    public ClipboardContentType ContentType { get; set; }

    public string? TextContent { get; set; }

    /// <summary>剪贴板中的 CF_HTML 原文（含 Version/StartHTML 头），用于回写保留富文本格式。</summary>
    public string? HtmlContent { get; set; }

    /// <summary>剪贴板中的 CF_RTF 原文，用于回写保留富文本格式。</summary>
    public string? RtfContent { get; set; }

    /// <summary>图片预览文件路径（仅图片类型）。</summary>
    public string? PreviewPath { get; set; }

    /// <summary>从图片中识别出的二维码内容。</summary>
    public string? QrContent { get; set; }

    /// <summary>字符数或文件大小。</summary>
    public long CharCount { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>置顶条目自定义排序序号（数字越小越靠前）。</summary>
    public int PinnedOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 文件条目的路径分隔符是入库时的换行。粘贴与复制必须同时认 <c>\r\n</c> 和 <c>\n</c>，
    /// 否则旧库或非 Windows 换行会被当成一条不存在的路径。
    /// </summary>
    public static readonly string[] FilePathSeparators = ["\r\n", "\n"];

    /// <summary>把文件条目的文本拆成路径列表；空行丢弃。</summary>
    public static string[] SplitFilePaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split(FilePathSeparators, StringSplitOptions.RemoveEmptyEntries);
    }
}
