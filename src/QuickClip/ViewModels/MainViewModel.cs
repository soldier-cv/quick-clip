using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuickClip.Models;
using QuickClip.Services;

namespace QuickClip.ViewModels;

/// <summary>主窗口视图模型：列表、搜索、筛选与动作分发。</summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppServices _services;
    private CancellationTokenSource? _searchDebounce;

    /// <summary>当前展示的卡片列表。</summary>
    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            DebounceRefresh();
        }
    }

    /// <summary>筛选索引：0 全部 / 1 文本 / 2 图片 / 3 链接。</summary>
    private int _filterIndex;
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (_filterIndex == value)
            {
                return;
            }

            _filterIndex = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    private ClipboardItemViewModel? _selectedItem;
    public ClipboardItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    private bool _isEmpty = true;
    /// <summary>当前列表是否无条目（用于空状态展示）。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (_isEmpty == value)
            {
                return;
            }

            _isEmpty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(ListVisibility));
        }
    }

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>空列表时的提示文案（区分无历史 / 筛选无结果）。</summary>
    public string EmptyHint
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_searchText) || _filterIndex != 0)
            {
                return "没有匹配的剪贴板条目\n试试清空搜索或切换筛选";
            }

            return "暂无剪贴板历史\n复制任意内容后，按 Win + V 即可在此查看";
        }
    }

    /// <summary>二维码 PNG 就绪（窗口展示覆盖层）。</summary>
    public event Action<byte[]>? QrImageReady;

    /// <summary>OCR 识别完成（窗口展示覆盖层）。</summary>
    public event Action<string>? OcrResultReady;

    /// <summary>关闭面板前记住的选中 id，再次 Win+V 时恢复。</summary>
    public long? RememberedSelectedId { get; set; }

    private CancellationTokenSource? _itemAddedDebounce;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _services.Pipeline.ItemAdded += OnItemAdded;
        _ = RefreshThenRepairAsync();
    }

    private async Task RefreshThenRepairAsync()
    {
        await RefreshAsync();
        try
        {
            if (await _services.RepairImageHistoryAsync())
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("历史图片补修失败", ex);
        }
    }

    public ClipboardItemViewModel? GetItemAt(int index) =>
        index >= 0 && index < Items.Count ? Items[index] : null;

    public async Task RefreshAsync()
    {
        int limit = _services.Settings.MaxHistoryItems;
        var items = await _services.Database.GetRecentAsync(limit);
        string query = _searchText;
        long? selectedId = SelectedItem?.Item.Id ?? RememberedSelectedId;

        var filtered = items
            .Where(i => FilterIndex switch
            {
                1 => i.ContentType is ClipboardContentType.Text or ClipboardContentType.Link,
                2 => i.ContentType == ClipboardContentType.Image,
                3 => i.ContentType == ClipboardContentType.Link,
                _ => true
            })
            .Where(i => SearchService.IsMatch(i, query))
            .ToList();

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Items.Clear();
            ClipboardItemViewModel? reselect = null;
            for (int i = 0; i < filtered.Count; i++)
            {
                var vm = new ClipboardItemViewModel(filtered[i]) { Index = i + 1 };
                Items.Add(vm);
                if (selectedId is long id && filtered[i].Id == id)
                {
                    reselect = vm;
                }
            }

            // 默认选中第 1 条（最近一条），便于 Enter 即贴
            SelectedItem = reselect ?? (Items.Count > 0 ? Items[0] : null);
            IsEmpty = Items.Count == 0;
            OnPropertyChanged(nameof(EmptyHint));
        });
    }

    /// <summary>清除今日历史后刷新列表。</summary>
    public async Task ClearTodayAndRefreshAsync()
    {
        int n = await _services.ClearTodayHistoryAsync();
        StatusText = n > 0 ? $"已清除今日 {n} 条" : "今日无非置顶历史";
        await RefreshAsync();
    }

    /// <summary>清空全部非置顶历史（置顶保留），并刷新列表。</summary>
    public async Task ClearAllUnpinnedAndRefreshAsync()
    {
        int n = await _services.ClearAllUnpinnedHistoryAsync();
        SelectedItem = null;
        RememberedSelectedId = null;
        StatusText = n > 0 ? $"已清空 {n} 条（置顶已保留）" : "没有可清空的条目";
        await RefreshAsync();
    }

    public void PasteSelected(bool plainOnly)
    {
        var selected = SelectedItem;
        if (selected == null)
        {
            return;
        }

        var item = selected.Item;
        _services.Pipeline.SuppressCapture(BuildDedupKeyHint(item));
        switch (item.ContentType)
        {
            case ClipboardContentType.Image:
                _services.Paste.PasteImage(item.PreviewPath);
                break;
            case ClipboardContentType.File:
                var files = item.TextContent?.Split(
                    new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                _services.Paste.PasteFiles(files);
                break;
            default:
                _services.Paste.PasteText(item.TextContent, plainOnly);
                break;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected == null)
        {
            return;
        }

        await _services.Database.DeleteAsync(selected.Item.Id);
        ThumbnailCache.RemoveByPath(selected.Item.PreviewPath);

        // 同步删除图片预览文件
        if (selected.IsImage && !string.IsNullOrEmpty(selected.Item.PreviewPath))
        {
            try
            {
                File.Delete(selected.Item.PreviewPath);
            }
            catch (Exception ex)
            {
                // 文件可能被占用，不影响条目删除
                DebugLog.LogException("删除预览图失败（可忽略）", ex);
            }
        }

        StatusText = "已删除";
        await RefreshAsync();
    }

    public async Task TogglePinSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected == null)
        {
            return;
        }

        bool pinned = !selected.Item.IsPinned;
        await _services.Database.TogglePinAsync(selected.Item.Id, pinned);
        selected.Item.IsPinned = pinned;
        StatusText = pinned ? "已置顶" : "已取消置顶";
        await RefreshAsync();
    }

    public async Task GenerateQrForSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected == null)
        {
            return;
        }

        string? content = selected.Item.ContentType switch
        {
            ClipboardContentType.Image when selected.HasQr => selected.QrText,
            ClipboardContentType.Image => null,
            _ => selected.Item.TextContent
        };

        if (string.IsNullOrWhiteSpace(content))
        {
            StatusText = "当前条目无法生成二维码";
            return;
        }

        var bytes = await Task.Run(() => _services.Qr.GeneratePng(content, 10));
        QrImageReady?.Invoke(bytes);
    }

    public async Task OcrSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected == null || !selected.IsImage)
        {
            StatusText = "非图片不支持 OCR 识别";
            return;
        }

        if (_services.Ocr.IsSystemEngine && !_services.Ocr.IsSupported)
        {
            StatusText = "系统 OCR 需要 Windows 10 及以上系统";
            return;
        }

        StatusText = "正在 OCR 识别…";
        var result = await _services.Ocr.RecognizeAsync(selected.Item.PreviewPath!);
        string? warning = _services.Ocr.LastWarning;

        if (string.IsNullOrWhiteSpace(result))
        {
            StatusText = !string.IsNullOrWhiteSpace(warning)
                ? warning
                : "未识别到文字";
            return;
        }

        // 有结果时仍展示降级提示（如 AI 失败后回退系统 OCR 成功）
        StatusText = !string.IsNullOrWhiteSpace(warning)
            ? $"{warning} · 已识别"
            : "OCR 完成";
        OcrResultReady?.Invoke(result);
    }

    /// <summary>将选中条目（文本 / 链接 / 图片 / 文件）覆盖到系统剪贴板，不自动粘贴。
    /// 回写剪贴板会触发监听，须抑制捕获，否则会在列表第一行再插一条相同记录。</summary>
    public async Task CopySelectedToClipboard()
    {
        var selected = SelectedItem;
        if (selected == null)
        {
            return;
        }

        var item = selected.Item;
        // 先抑制再写：双击时 MouseUp 复制 + DoubleClick 粘贴会连续写两次剪贴板
        _services.Pipeline.SuppressCapture(BuildDedupKeyHint(item));
        try
        {
            switch (item.ContentType)
            {
                case ClipboardContentType.Image:
                    await _services.Paste.CopyImageAsync(item.PreviewPath);
                    break;
                case ClipboardContentType.File:
                    var files = item.TextContent?.Split(
                        new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    await _services.Paste.CopyFilesAsync(files);
                    break;
                default:
                    await _services.Paste.CopyTextAsync(item.TextContent);
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("复制到剪贴板失败", ex);
            StatusText = "复制失败，剪贴板可能被占用";
            return;
        }

        StatusText = "已复制";
    }

    /// <summary>将已识别二维码的文本覆盖到系统剪贴板（纯文本），不新增历史。</summary>
    public async Task CopyQrTextAsync()
    {
        var selected = SelectedItem;
        if (selected == null || !selected.HasQr)
        {
            StatusText = "当前条目没有可解析的二维码";
            return;
        }

        string text = selected.QrText;
        _services.Pipeline.SuppressCapture("text:" + text);
        try
        {
            await _services.Paste.CopyTextAsync(text);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("复制二维码文本失败", ex);
            StatusText = "复制失败，剪贴板可能被占用";
            return;
        }

        StatusText = "已复制二维码文本";
    }

    /// <summary>与捕获侧近似的去重键提示（文本精确；文件/图片靠 Suppress 时间窗）。</summary>
    private static string? BuildDedupKeyHint(ClipboardItem item) =>
        item.ContentType switch
        {
            ClipboardContentType.Text or ClipboardContentType.Link
                => string.IsNullOrEmpty(item.TextContent) ? null : "text:" + item.TextContent,
            _ => null
        };

    private void DebounceRefresh()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _ = RefreshAsync();
            }
        }, TaskScheduler.Default);
    }

    private void OnItemAdded(ClipboardItem item)
    {
        // 连续复制：合并刷新，避免每条都 Clear 列表导致闪烁
        _itemAddedDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _itemAddedDebounce = cts;
        _ = DebouncedOnItemAddedAsync(item, cts.Token);
    }

    private async Task DebouncedOnItemAddedAsync(ClipboardItem item, CancellationToken token)
    {
        try
        {
            await Task.Delay(280, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = "已捕获";

            // 搜索/筛选中：只提示，不打断列表；否则顶部插入，尽量不全量重建
            if (!string.IsNullOrWhiteSpace(_searchText) || FilterIndex != 0)
            {
                return;
            }

            // 若已有同 id 则全量刷新即可
            if (Items.Any(x => x.Item.Id == item.Id))
            {
                _ = RefreshAsync();
                return;
            }

            // 列表顺序与库一致：置顶在前，其余按时间；新捕获默认非置顶，
            // 必须插在「最后一个置顶」之后，不能 Insert(0) 盖过置顶区。
            int insertAt = 0;
            if (!item.IsPinned)
            {
                while (insertAt < Items.Count && Items[insertAt].IsPinned)
                {
                    insertAt++;
                }
            }

            var vm = new ClipboardItemViewModel(item);
            Items.Insert(insertAt, vm);
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Index = i + 1;
            }

            // 超过上限时从 UI 尾部去掉非置顶
            int max = _services.Settings.MaxHistoryItems;
            while (Items.Count > max)
            {
                var last = Items[^1];
                if (last.IsPinned)
                {
                    break;
                }

                Items.RemoveAt(Items.Count - 1);
            }

            SelectedItem = vm;
            IsEmpty = Items.Count == 0;
            OnPropertyChanged(nameof(EmptyHint));
        });
    }

    public void Dispose()
    {
        _services.Pipeline.ItemAdded -= OnItemAdded;
        _searchDebounce?.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}




