using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using QuickClip.Models;
using QuickClip.Services;
using Wpf.Ui.Controls;

namespace QuickClip.ViewModels;

/// <summary>剪贴板条目的卡片展示模型。</summary>
public sealed class ClipboardItemViewModel : INotifyPropertyChanged
{
    public ClipboardItem Item { get; private set; }

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

    /// <summary>文本/链接可生成二维码；已识别二维码的图片可解析复制文本。</summary>
    public bool ShowQrAction => IsText || HasQr;

    public SymbolRegular QrActionIcon =>
        IsImage && HasQr ? SymbolRegular.ScanQrCode24 : SymbolRegular.QrCode24;

    public string QrActionToolTip =>
        IsImage && HasQr ? "复制二维码文本" : "悬停预览二维码 · 点击放大";

    public bool IsPinned => Item.IsPinned;

    public bool IsImage => Item.ContentType == ClipboardContentType.Image;

    private bool _isOcrBusy;

    /// <summary>该条目正在 OCR，按钮禁用并显示环状等待，识别结束才恢复。</summary>
    public bool IsOcrBusy
    {
        get => _isOcrBusy;
        set
        {
            if (_isOcrBusy == value)
            {
                return;
            }

            _isOcrBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOcrEnabled));
            OnPropertyChanged(nameof(OcrToolTip));
        }
    }

    public bool IsOcrEnabled => !_isOcrBusy;

    public string OcrToolTip => _isOcrBusy ? "正在识别…" : "OCR";

    /// <summary>文本/链接/文件条目（有全文可预览）。</summary>
    public bool IsTextual => Item.ContentType != ClipboardContentType.Image;

    public bool IsText => Item.ContentType is ClipboardContentType.Text or ClipboardContentType.Link;

    public bool IsFile => Item.ContentType == ClipboardContentType.File;

    /// <summary>仅针对截图/图片提供贴图置顶动作。</summary>
    public bool ShowStickyAction => IsImage;

    /// <summary>是否支持文本翻译。</summary>
    public bool ShowTranslateAction => IsText && !string.IsNullOrWhiteSpace(Item.TextContent);

    private bool _isTranslating;
    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            if (_isTranslating == value) return;
            _isTranslating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTranslateEnabled));
            OnPropertyChanged(nameof(TranslateDrawerVisibility));
        }
    }

    public bool IsTranslateEnabled => !_isTranslating;

    private bool _isTranslated;
    public bool IsTranslated
    {
        get => _isTranslated;
        set
        {
            if (_isTranslated == value) return;
            _isTranslated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TranslateDrawerVisibility));
        }
    }

    public Visibility TranslateDrawerVisibility => (_isTranslated || _isTranslating) ? Visibility.Visible : Visibility.Collapsed;

    private string _translatedText = string.Empty;
    public string TranslatedText
    {
        get => _translatedText;
        set
        {
            if (_translatedText == value) return;
            _translatedText = value;
            OnPropertyChanged();
        }
    }

    private string _translationEngineInfo = string.Empty;
    public string TranslationEngineInfo
    {
        get => _translationEngineInfo;
        set
        {
            if (_translationEngineInfo == value) return;
            _translationEngineInfo = value;
            OnPropertyChanged();
        }
    }

    public SymbolRegular TypeIcon => Item.ContentType switch
    {
        ClipboardContentType.Link => SymbolRegular.Link24,
        ClipboardContentType.Image => SymbolRegular.Image24,
        ClipboardContentType.File => SymbolRegular.Folder24,
        _ => SymbolRegular.Document24
    };

    private volatile bool _thumbnailLoading;

    /// <summary>
    /// 图片缩略图：非阻塞异步加载。
    /// 优先从内存 LRU 缓存获取；未命中时立即返回 null，后台线程池解码并 Freeze 后通过 Dispatcher 刷新，
    /// 既保证 LRU 淘汰机制不失效（不产生长久强引用持有），又杜绝 UI 线程滚动卡顿。
    /// </summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (!IsImage)
            {
                return null;
            }

            string? path = Item.PreviewPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            if (ThumbnailCache.TryGet(path, 240, out var cached))
            {
                return cached;
            }

            if (!_thumbnailLoading)
            {
                _thumbnailLoading = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var img = ThumbnailCache.GetOrCreate(path, 240);
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                        {
                            dispatcher.BeginInvoke(() =>
                            {
                                _thumbnailLoading = false;
                                OnPropertyChanged(nameof(Thumbnail));
                            });
                        }
                        else
                        {
                            _thumbnailLoading = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _thumbnailLoading = false;
                        DebugLog.LogException("异步加载缩略图异常", ex);
                    }
                });
            }

            return null;
        }
    }

    /// <summary>悬浮预览大图（仅在打开 ToolTip 时才解码）。</summary>
    public BitmapImage? HoverThumbnail => LoadThumbnail(720);

    /// <summary>悬浮预览全文（文本/链接/文件），图片为 null。</summary>
    public string? HoverText { get; }

    private BitmapImage? LoadThumbnail(int decodePixelWidth)
    {
        if (!IsImage)
        {
            return null;
        }

        string? path = Item.PreviewPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        return ThumbnailCache.GetOrCreate(path, decodePixelWidth);
    }

    /// <summary>更新底层条目数据并触发必要绑定的属性通知，用于列表刷新时复用 ViewModel。</summary>
    public void Update(ClipboardItem item)
    {
        bool pinnedChanged = Item.IsPinned != item.IsPinned;
        bool qrChanged = Item.QrContent != item.QrContent;
        Item = item;

        if (pinnedChanged)
        {
            OnPropertyChanged(nameof(IsPinned));
        }

        if (qrChanged)
        {
            OnPropertyChanged(nameof(HasQr));
            OnPropertyChanged(nameof(QrText));
            OnPropertyChanged(nameof(ShowQrAction));
            OnPropertyChanged(nameof(QrActionIcon));
            OnPropertyChanged(nameof(QrActionToolTip));
        }
    }

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
            return !string.IsNullOrEmpty(item.QrContent) ? "已识别二维码" : "图片预览";
        }

        if (item.ContentType == ClipboardContentType.File)
        {
            var paths = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0)
            {
                return "空文件列表";
            }

            if (paths.Length == 1)
            {
                string name = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? paths[0] : name;
            }

            var names = paths.Take(3).Select(p =>
            {
                string n = Path.GetFileName(p.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(n) ? p : n;
            });
            string joinedNames = string.Join(", ", names);
            return paths.Length > 3
                ? $"{paths.Length} 个文件: {joinedNames} …"
                : $"{paths.Length} 个文件: {joinedNames}";
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

    public bool HasSizeText => !string.IsNullOrEmpty(SizeText);

    private static string BuildSize(ClipboardItem item)
    {
        return item.ContentType switch
        {
            ClipboardContentType.Image => FormatBytes(item.CharCount),
            ClipboardContentType.File => BuildFileMeta(item),
            ClipboardContentType.Link => string.Empty,
            _ => $"{item.CharCount:N0} 字符"
        };
    }

    /// <summary>
    /// 文件元数据：单文件返回如「1.3 KB」，多文件返回如「3 项 · 12.3 MB」，避免与 TypeLabel(文件) 重复。
    /// </summary>
    private static string BuildFileMeta(ClipboardItem item)
    {
        var paths = (item.TextContent ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        int count = paths.Length;
        if (count <= 0)
        {
            return string.Empty;
        }

        long bytes = item.CharCount;
        string sizeLabel = bytes > 0 && bytes != count ? FormatBytes(bytes) : string.Empty;

        if (count == 1)
        {
            return sizeLabel;
        }

        return !string.IsNullOrEmpty(sizeLabel)
            ? $"{count} 项 · {sizeLabel}"
            : $"{count} 项";
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



