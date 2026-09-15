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

    /// <summary>当前选中的主栏目：0 = 剪贴历史，1 = 常用短语。</summary>
    private int _selectedMainTab;
    public int SelectedMainTab
    {
        get => _selectedMainTab;
        set
        {
            if (_selectedMainTab == value) return;
            _selectedMainTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHistoryTabActive));
            OnPropertyChanged(nameof(IsSnippetsTabActive));
            OnPropertyChanged(nameof(HistoryVisibility));
            OnPropertyChanged(nameof(SnippetsVisibility));
            if (value == 1)
            {
                _ = RefreshSnippetsAsync();
            }
        }
    }

    public bool IsHistoryTabActive => SelectedMainTab == 0;
    public bool IsSnippetsTabActive => SelectedMainTab == 1;

    public Visibility HistoryVisibility => IsHistoryTabActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SnippetsVisibility => IsSnippetsTabActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>常用短语集合。</summary>
    public ObservableCollection<SnippetItem> Snippets { get; } = new();

    /// <summary>常用短语分类集合。</summary>
    public ObservableCollection<string> SnippetCategories { get; } = new();

    private string _selectedSnippetCategory = "全部";
    public string SelectedSnippetCategory
    {
        get => _selectedSnippetCategory;
        set
        {
            if (_selectedSnippetCategory == value) return;
            _selectedSnippetCategory = value;
            OnPropertyChanged();
            _ = RefreshSnippetsAsync();
        }
    }

    private SnippetItem? _selectedSnippet;
    public SnippetItem? SelectedSnippet
    {
        get => _selectedSnippet;
        set
        {
            if (_selectedSnippet == value) return;
            _selectedSnippet = value;
            OnPropertyChanged();
        }
    }

    /// <summary>收集栈状态与文本。</summary>
    public bool IsStackActive => _services.StackPaste.IsActive;
    public int StackCount => _services.StackPaste.Count;
    public string StackButtonText => IsStackActive ? $"收集栈 ({StackCount}) 已开启" : "收集栈";
    public string StackButtonTooltip => IsStackActive
        ? "连续粘贴栈已开启：按 Ctrl+V 逐项粘贴，点击此处关闭"
        : "连续粘贴栈模式（开启后按 Ctrl+V 逐项粘贴，可随时再次点击关闭）";

    /// <summary>二维码 PNG 就绪（窗口展示覆盖层）。</summary>
    public event Action<byte[]>? QrImageReady;

    /// <summary>OCR 识别完成（窗口展示覆盖层：标题, 正文）。</summary>
    public event Action<string, string>? OcrResultReady;

    private CancellationTokenSource? _itemAddedDebounce;
    private readonly HashSet<long> _ocrBusyIds = new();
    private readonly HashSet<long> _deletingIds = new();

    /// <summary>收集栈状态变化订阅（保存引用以便 Dispose 时解除）。</summary>
    private readonly Action<bool, int> _stackStateChangedHandler;

    /// <summary>刷新代际：并发刷新时只有最新一次的结果可以落到 UI（避免旧搜索结果覆盖新结果）。</summary>
    private int _refreshGeneration;

    /// <summary>
    /// 面板可见时置 true：新捕获不再抢占当前选中项，
    /// 否则用户正准备按 Enter 时后台捕获会把选中项换成刚复制的那条。
    /// </summary>
    public bool SuppressAutoSelect { get; set; }

    public MainViewModel(AppServices services)
    {
        _services = services;
        _services.Pipeline.ItemAdded += OnItemAdded;
        _stackStateChangedHandler = (active, count) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(IsStackActive));
                OnPropertyChanged(nameof(StackCount));
                OnPropertyChanged(nameof(StackButtonText));
                OnPropertyChanged(nameof(StackButtonTooltip));
            });
        };
        _services.StackPaste.StateChanged += _stackStateChangedHandler;
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
        int generation = Interlocked.Increment(ref _refreshGeneration);
        int limit = _services.Settings.MaxHistoryItems;
        int filterIndex = FilterIndex;
        string query = _searchText;
        long? selectedId = SelectedItem?.Item.Id;

        var items = await _services.Database.GetRecentAsync(limit);

        var filtered = items
            .Where(i => filterIndex switch
            {
                1 => i.ContentType is ClipboardContentType.Text or ClipboardContentType.Link,
                2 => i.ContentType == ClipboardContentType.Image,
                3 => i.ContentType == ClipboardContentType.Link,
                _ => true
            })
            .Where(i => SearchService.IsMatch(i, query))
            .ToList();

        // 期间又发起了更新的刷新：本次结果已过期，丢弃
        if (generation != Volatile.Read(ref _refreshGeneration))
        {
            DebugLog.LogDetail($"丢弃过期的列表刷新（gen={generation}）");
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Items.Clear();
            ClipboardItemViewModel? reselect = null;
            for (int i = 0; i < filtered.Count; i++)
            {
                var vm = new ClipboardItemViewModel(filtered[i]) { Index = i + 1 };
                if (_ocrBusyIds.Contains(filtered[i].Id))
                {
                    vm.IsOcrBusy = true;
                }

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
        // 自身回写由剪贴板序列号识别，这里不再需要抑制窗口
        switch (item.ContentType)
        {
            case ClipboardContentType.Image:
                _services.Paste.PasteImage(item.PreviewPath);
                break;
            case ClipboardContentType.File:
                if (plainOnly)
                {
                    _services.Paste.PasteText(item.TextContent, plainOnly: true);
                }
                else
                {
                    var files = item.TextContent?.Split(
                        new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    _services.Paste.PasteFiles(files);
                }
                break;
            default:
                // 非纯文本粘贴时带上原剪贴板的 HTML/RTF，保留格式（没有富文本则退化为纯文本）
                _services.Paste.PasteText(
                    item.TextContent,
                    plainOnly,
                    plainOnly ? null : item.HtmlContent,
                    plainOnly ? null : item.RtfContent);
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

        // 重入保护：双击「删除」按钮 / 连按 Delete 时，第二次点击可能已落在
        // 被虚拟化回收并重新绑定的卡片上，会把相邻条目一起删掉。
        if (!_deletingIds.Add(selected.Item.Id))
        {
            DebugLog.LogDetail($"忽略重复删除请求: id={selected.Item.Id}");
            return;
        }

        try
        {
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
        finally
        {
            _deletingIds.Remove(selected.Item.Id);
        }
    }

    public async Task TogglePinSelectedAsync(ClipboardItemViewModel? target = null)
    {
        var selected = target ?? SelectedItem;
        if (selected == null)
        {
            return;
        }

        bool pinned = !selected.Item.IsPinned;
        await _services.Database.TogglePinAsync(selected.Item.Id, pinned);
        selected.Item.IsPinned = pinned;
        StatusText = pinned ? "已置顶" : "已取消置顶";
        SelectedItem = selected;
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

    public async Task OcrSelectedAsync(ClipboardItemViewModel? target = null)
    {
        var selected = target ?? SelectedItem;
        if (selected == null || !selected.IsImage)
        {
            StatusText = "非图片不支持 OCR 识别";
            return;
        }

        if (selected.IsOcrBusy || !_ocrBusyIds.Add(selected.Item.Id))
        {
            return;
        }

        if (_services.Ocr.IsSystemEngine && !_services.Ocr.IsSupported)
        {
            _ocrBusyIds.Remove(selected.Item.Id);
            StatusText = "系统 OCR 需要 Windows 10 及以上系统";
            return;
        }

        selected.IsOcrBusy = true;
        StatusText = "正在 OCR 识别…";
        try
        {
            var result = await _services.Ocr.RecognizeAsync(selected.Item.PreviewPath!);
            string? warning = _services.Ocr.LastWarning;
            string title = _services.Ocr.LastEngineTitle;

            if (string.IsNullOrWhiteSpace(result))
            {
                StatusText = !string.IsNullOrWhiteSpace(warning)
                    ? warning
                    : "未识别到文字";
                // 失败也弹层，避免状态栏被截断、误以为没反应
                OcrResultReady?.Invoke(title, warning ?? "未识别到文字");
                return;
            }

            // 有结果时仍展示降级提示（如 AI 失败后回退系统 OCR 成功）
            StatusText = !string.IsNullOrWhiteSpace(warning)
                ? $"{warning} · 已识别"
                : "OCR 完成";
            string body = string.IsNullOrWhiteSpace(warning)
                ? result
                : warning + Environment.NewLine + Environment.NewLine + result;
            OcrResultReady?.Invoke(title, body);
        }
        finally
        {
            _ocrBusyIds.Remove(selected.Item.Id);
            void ClearBusy()
            {
                var live = Items.FirstOrDefault(item => item.Item.Id == selected.Item.Id) ?? selected;
                live.IsOcrBusy = false;
            }

            if (System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                ClearBusy();
            }
            else
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(ClearBusy);
            }
        }
    }

    /// <summary>复制当前选中项内容到系统剪贴板（自身回写由剪贴板序列号识别，不会重复入库）。</summary>
    public Task CopySelectedToClipboard() =>
        CopyItemToClipboardAsync(SelectedItem);

    /// <summary>将指定条目内容复制到系统剪贴板。</summary>
    public async Task CopyItemToClipboardAsync(ClipboardItemViewModel? target)
    {
        if (target == null)
        {
            return;
        }

        var item = target.Item;
        try
        {
            bool ok = item.ContentType switch
            {
                ClipboardContentType.Image => await _services.Paste.CopyImageAsync(item.PreviewPath),
                ClipboardContentType.File => await _services.Paste.CopyFilesAsync(
                    item.TextContent?.Split(
                        new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)),
                _ => await _services.Paste.CopyTextAsync(
                    item.TextContent,
                    plainOnly: false,
                    html: item.HtmlContent,
                    rtf: item.RtfContent)
            };

            StatusText = ok ? "已复制" : "复制失败";
        }
        catch (Exception ex)
        {
            DebugLog.LogException("复制到剪贴板失败", ex);
            StatusText = "复制失败，剪贴板可能被占用";
        }
    }

    /// <summary>将文件列表中的纯文件名以换行形式复制到系统剪贴板。</summary>
    public async Task CopyFileNamesAsync(ClipboardItemViewModel? target)
    {
        if (target == null)
        {
            return;
        }

        var item = target.Item;
        if (item.ContentType != ClipboardContentType.File || string.IsNullOrEmpty(item.TextContent))
        {
            return;
        }

        var paths = item.TextContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var names = paths.Select(p =>
        {
            string? n = Path.GetFileName(p);
            return string.IsNullOrEmpty(n) ? p : n;
        });
        string joinedNames = string.Join(Environment.NewLine, names);

        StatusText = await _services.Paste.CopyTextAsync(joinedNames, plainOnly: true)
            ? "已复制文件名"
            : "复制失败";
    }

    /// <summary>将文件项的完整绝对路径以纯文本形式复制到系统剪贴板。</summary>
    public async Task CopyFilePathAsync(ClipboardItemViewModel? target)
    {
        if (target == null)
        {
            return;
        }

        var item = target.Item;
        if (item.ContentType != ClipboardContentType.File || string.IsNullOrEmpty(item.TextContent))
        {
            return;
        }

        StatusText = await _services.Paste.CopyTextAsync(item.TextContent, plainOnly: true)
            ? "已复制全路径"
            : "复制失败";
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

        try
        {
            StatusText = await _services.Paste.CopyTextAsync(selected.QrText)
                ? "已复制二维码文本"
                : "复制失败";
        }
        catch (Exception ex)
        {
            DebugLog.LogException("复制二维码文本失败", ex);
            StatusText = "复制失败，剪贴板可能被占用";
        }
    }

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

            // 面板可见（用户正在挑选）时不抢占选中项，否则 Enter 会粘到刚捕获的那条
            if (!SuppressAutoSelect)
            {
                SelectedItem = vm;
            }

            IsEmpty = Items.Count == 0;
            OnPropertyChanged(nameof(EmptyHint));
        });
    }

    #region 常用短语 (Snippets)

    public async Task RefreshSnippetsAsync()
    {
        try
        {
            var categories = await _services.Database.GetSnippetCategoriesAsync();
            // 仅在分类集合确实变化时重建，避免 Clear+重填 把 ComboBox 的当前选中项冲掉
            if (!categories.SequenceEqual(SnippetCategories))
            {
                SnippetCategories.Clear();
                foreach (var cat in categories)
                {
                    SnippetCategories.Add(cat);
                }
            }

            // 当前筛选分类已不存在（如末条被删），回退到「全部」；直接改字段避免再次触发刷新
            if (!SnippetCategories.Contains(_selectedSnippetCategory))
            {
                _selectedSnippetCategory = "全部";
                OnPropertyChanged(nameof(SelectedSnippetCategory));
            }

            // 常用短语复用剪贴历史的搜索框不可见，避免用历史搜索词误过滤短语列表
            var list = await _services.Database.GetSnippetsAsync(_selectedSnippetCategory, null);

            Snippets.Clear();
            foreach (var snip in list)
            {
                Snippets.Add(snip);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("刷新常用短语失败", ex);
        }
    }

    public Task PasteSnippetAsync(SnippetItem snippet, bool plainOnly = false)
    {
        if (snippet == null) return Task.CompletedTask;

        string text = ResolveSnippetText(snippet);
        _services.Paste.RememberTargetWindow();
        _services.Paste.PasteText(text, plainOnly);
        return Task.CompletedTask;
    }

    /// <summary>解析短语动态占位符；{clipboard} 以当前系统剪贴板文本填充。</summary>
    public string ResolveSnippetText(SnippetItem snippet)
    {
        if (snippet == null) return string.Empty;

        string? currentClip = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                currentClip = System.Windows.Clipboard.GetText();
            }
        }
        catch
        {
            // 忽略剪贴板占用
        }

        return snippet.ResolveContent(currentClip);
    }

    public async Task AddSnippetAsync(SnippetItem item)
    {
        await _services.Database.AddSnippetAsync(item);
        await RefreshSnippetsAsync();
    }

    public async Task UpdateSnippetAsync(SnippetItem item)
    {
        await _services.Database.UpdateSnippetAsync(item);
        await RefreshSnippetsAsync();
    }

    public async Task DeleteSnippetAsync(SnippetItem item)
    {
        if (item == null) return;
        await _services.Database.DeleteSnippetAsync(item.Id);
        await RefreshSnippetsAsync();
    }

    #endregion

    #region 桌面贴图 (Sticky)

    public bool PinItemToDesktop(ClipboardItemViewModel? vm)
    {
        if (vm == null) return false;
        return _services.Sticky.PinItem(vm.Item);
    }

    public bool PinCurrentClipboardToDesktop()
    {
        return _services.Sticky.PinFromCurrentClipboard();
    }

    #endregion

    #region 文本翻译 (Translation)

    public async Task ToggleTranslateItemAsync(ClipboardItemViewModel vm)
    {
        if (vm == null || string.IsNullOrWhiteSpace(vm.Item.TextContent)) return;

        if (vm.IsTranslated)
        {
            vm.IsTranslated = false;
            return;
        }

        if (vm.IsTranslating)
        {
            return;
        }

        vm.TranslatedText = string.Empty;
        vm.TranslationEngineInfo = string.Empty;
        vm.IsTranslating = true;
        try
        {
            var res = await _services.Translation.TranslateAsync(vm.Item.TextContent);
            vm.IsTranslating = false;
            if (res.Success)
            {
                vm.TranslatedText = res.TranslatedText;
                vm.TranslationEngineInfo = res.Engine;
                vm.IsTranslated = true;
            }
            else
            {
                vm.TranslatedText = $"翻译失败: {res.ErrorMessage}";
                vm.TranslationEngineInfo = "失败";
                vm.IsTranslated = true;
            }
        }
        catch (Exception ex)
        {
            vm.IsTranslating = false;
            vm.TranslatedText = $"翻译异常: {ex.Message}";
            vm.IsTranslated = true;
        }
    }

    public async Task<string> TranslateRawTextAsync(string text)
    {
        var res = await _services.Translation.TranslateAsync(text);
        return res.Success ? res.TranslatedText : $"翻译失败: {res.ErrorMessage}";
    }

    #endregion

    #region 收集栈 (Stack Paste)

    public void ToggleStackMode()
    {
        _services.StackPaste.Toggle();
    }

    public void SplitLinesToStack()
    {
        _services.StackPaste.SplitClipboardLines();
    }

    public void PushItemToStack(ClipboardItemViewModel vm)
    {
        if (vm == null) return;
        if (!_services.StackPaste.IsActive)
        {
            _services.StackPaste.Start();
        }
        _services.StackPaste.Push(vm.Item);
    }

    #endregion

    public void Dispose()
    {
        _services.Pipeline.ItemAdded -= OnItemAdded;
        _services.StackPaste.StateChanged -= _stackStateChangedHandler;
        _searchDebounce?.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}




