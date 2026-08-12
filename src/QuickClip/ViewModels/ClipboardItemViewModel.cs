using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using QuickClip.Models;
using QuickClip.Services;
using Wpf.Ui.Controls;

namespace QuickClip.ViewModels;

/// <summary>剪贴板条目的卡片展示模型。</summary>
public sealed class ClipboardItemViewModel : INotifyPropertyChanged
{
    public ClipboardItem Item { get; }

    private int _index;

    /// <summary>列表序号（1~9 快速粘贴）。</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IndexText));
        }
    }

    public string IndexText => Index.ToString();

    public string TypeLabel { get; }

    /// <summary>卡片主文案（单行截断）。</summary>
    public string PreviewText { get; }

    public string TimeText { get; }

    public string SizeText { get; }

    public bool HasQr => !string.IsNullOrEmpty(Item.QrContent);

    public string QrText => Item.QrContent ?? string.Empty;

    public bool IsPinned => Item.IsPinned;

    public bool IsImage => Item.ContentType == ClipboardContentType.Image;

    /// <summary>文本/链接/文件条目（有全文可预览）。</summary>
    public bool IsTextual => Item.ContentType != ClipboardContentType.Image;

    public bool IsText => Item.ContentType is ClipboardContentType.Text or ClipboardContentType.Link;

    public bool IsLink => Item.ContentType == ClipboardContentType.Link;

    public bool IsFile => Item.ContentType == ClipboardContentType.File;

    public SymbolRegular TypeIcon => Item.ContentType switch
    {
        ClipboardContentType.Link => SymbolRegular.Link24,
        ClipboardContentType.Image => SymbolRegular.Image24,
        ClipboardContentType.File => SymbolRegular.Folder24,
        _ => SymbolRegular.Document24
    };

    private readonly Lazy<BitmapImage?> _thumbnail;

    /// <summary>图片缩略图（延迟加载，仅列表项实际渲染时解码，避免一次性解码大量图片）。</summary>
    public BitmapImage? Thumbnail => _thumbnail.Value;

    /// <summary>悬浮预览大图（仅在打开 ToolTip 时才解码）。</summary>
    public BitmapImage? HoverThumbnail => _hoverThumbnail.Value;

    /// <summary>悬浮预览全文（文本/链接/文件），图片为 null。</summary>
    public string? HoverText { get; }

    private readonly Lazy<BitmapImage?> _hoverThumbnail;

    public ClipboardItemViewModel(ClipboardItem item)
    {
        Item = item;
        TypeLabel = item.ContentType switch
        {
            ClipboardContentType.Link => "链接",
            ClipboardContentType.Image => "图片",
            ClipboardContentType.File => "文件",
            _ => "文本"
        };

        PreviewText = BuildPreview(item);
        TimeText = BuildTime(item.CreatedAt);
        SizeText = BuildSize(item);
        HoverText = item.ContentType == ClipboardContentType.Image
            ? null
            : item.TextContent;

        // 延迟解码 + LRU 缓存，虚拟化滚出后可被淘汰
        _thumbnail = new Lazy<BitmapImage?>(() =>
            IsImage && !string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath)
                ? ThumbnailCache.GetOrCreate(item.PreviewPath!, 240)
                : null);

        _hoverThumbnail = new Lazy<BitmapImage?>(() =>
            IsImage && !string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath)
                ? ThumbnailCache.GetOrCreate(item.PreviewPath!, 720)
                : null);
    }

    public void RefreshDisplay()
    {
        // 供置顶/删除后刷新用（占位，属性均不可变则无需实现）
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string BuildPreview(ClipboardItem item)
    {
        string text = item.TextContent ?? string.Empty;
        if (item.ContentType == ClipboardContentType.Image)
        {
            return !string.IsNullOrEmpty(item.QrContent) ? $"已识别二维码: {item.QrContent}" : "图片预览";
        }

        string oneLine = text.Replace("\r\n", " / ").Replace('\n', ' ').Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + " …";
    }

    private static string BuildTime(DateTime time)
    {
        var now = DateTime.Now;
        if (time.Date == now.Date)
        {
            return time.ToString("HH:mm");
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return "昨天 " + time.ToString("HH:mm");
        }

        return time.ToString("M月d日 HH:mm");
    }

    private static string BuildSize(ClipboardItem item)
    {
        return item.ContentType switch
        {
            ClipboardContentType.Image => FormatBytes(item.CharCount),
            ClipboardContentType.File => BuildFileMeta(item),
            ClipboardContentType.Link => "链接",
            _ => $"{item.CharCount:N0} 字符"
        };
    }

    /// <summary>
    /// 文件元数据：个数来自路径行；CharCount 新数据为总字节，旧数据可能为个数。
    /// 展示如「2 个文件 · 12.3 MB」，避免「文件 · 1234567 个文件」。
    /// </summary>
    private static string BuildFileMeta(ClipboardItem item)
    {
        int count = CountPaths(item.TextContent);
        if (count < 1)
        {
            count = 1;
        }

        string countLabel = $"{count} 个文件";
        long n = item.CharCount;
        // 旧记录：CharCount == 文件个数；新记录：总字节（通常远大于个数）
        if (n <= 0 || n == count)
        {
            return countLabel;
        }

        return $"{countLabel} · {FormatBytes(n)}";
    }

    private static int CountPaths(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.0} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }

        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

}



