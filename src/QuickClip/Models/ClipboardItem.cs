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

    /// <summary>图片预览文件路径（仅图片类型）。</summary>
    public string? PreviewPath { get; set; }

    /// <summary>从图片中识别出的二维码内容。</summary>
    public string? QrContent { get; set; }

    /// <summary>字符数或文件大小。</summary>
    public long CharCount { get; set; }

    public bool IsPinned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
