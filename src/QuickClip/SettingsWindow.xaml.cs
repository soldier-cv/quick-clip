using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Models;
using QuickClip.Services;
using MediaBrush = System.Windows.Media.Brush;

namespace QuickClip;

/// <summary>
/// 设置窗口：主题、快捷键、系统剪贴板冲突管理、开机自启动、OCR、高级、关于。
/// 使用自绘铬普通 Window，避免 FluentWindow 客户区白边。
/// 
/// @author xudong.hua,gemini
/// @since 2026-08-19 16:00 星期三
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private bool _busy;
    private bool _suppressUiEvents;
    private bool _themeBoxReady;

    /// <summary>主题下拉项（色块 + 名称）。</summary>
    private sealed class ThemeOption
    {
        public AppTheme Id { get; init; }
        public string Name { get; init; } = "";
        public SolidColorBrush Swatch { get; init; } = new(Colors.Gray);
    }

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;

        WindowChromeHelper.Apply(this, RootGrid);
        FillThemeBox();

        RefreshUi();
        RefreshUpdatePanel();
        _services.Settings.Changed += OnSettingsChanged;
        _services.Update.PendingChanged += OnPendingUpdateChanged;
        _services.Update.DownloadFailedChanged += OnDownloadFailedChanged;
        ThemeService.Changed += OnThemeServiceChanged;
        Loaded += OnSettingsWindowLoaded;
        Closed += (_, _) =>
        {
            _services.Settings.Changed -= OnSettingsChanged;
            _services.Update.PendingChanged -= OnPendingUpdateChanged;
            _services.Update.DownloadFailedChanged -= OnDownloadFailedChanged;
            ThemeService.Changed -= OnThemeServiceChanged;
            Loaded -= OnSettingsWindowLoaded;
        };
    }

    private void OnSettingsWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 首次布局完成后按实际有限高度重算滚动范围
        RefreshScrollExtent();
        // 再排一帧：TitleBar/WindowChrome 有时第二帧才稳定客户区高度
        Dispatcher.BeginInvoke(RefreshScrollExtent, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 按当前窗口客户区有限高度重测 ScrollViewer extent。
    /// 禁止在外部对窗口 Measure(∞)，否则会把 ScrollableHeight 算成 0。
    /// </summary>
    public void RefreshScrollExtent()
    {
        if (SettingsScrollViewer is null)
        {
            return;
        }

        // 只使内容树失效重测，不要对 Window 本身 Measure/Arrange（会干扰位置）
        RootGrid?.InvalidateMeasure();
        RootGrid?.InvalidateArrange();
        SettingsScrollViewer.InvalidateMeasure();
        SettingsScrollViewer.InvalidateArrange();
        UpdateLayout();
    }

    /// <summary>
    /// 下拉未展开时，滚轮交给页面滚动，避免悬停 ComboBox 时误改选项。
    /// </summary>
    private void OnComboBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo)
        {
            return;
        }

        // 下拉列表打开时仍用滚轮选中项
        if (combo.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;
        if (SettingsScrollViewer is null)
        {
            return;
        }

        // 与系统滚轮方向一致：滚轮上滚 → 内容上移（offset 减小）
        double offset = SettingsScrollViewer.VerticalOffset - e.Delta;
        offset = Math.Max(0, Math.Min(offset, SettingsScrollViewer.ScrollableHeight));
        SettingsScrollViewer.ScrollToVerticalOffset(offset);
    }

    private void FillThemeBox()
    {
        ThemeBox.Items.Clear();
        foreach (ThemePalette palette in ThemePalette.All)
        {
            var brush = new SolidColorBrush(palette.Accent);
            brush.Freeze();
            ThemeBox.Items.Add(new ThemeOption
            {
                Id = palette.Id,
                Name = palette.DisplayName,
                Swatch = brush
            });
        }

        _themeBoxReady = true;
    }

    private void OnThemeServiceChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        // 主题已通过 DynamicResource 刷新；补刷窗口底色与状态徽章
        WindowChromeHelper.Apply(this, RootGrid);
        RefreshSysClipboardStatus();
    }

    private void RefreshUi()
    {
        _suppressUiEvents = true;
        try
        {
            var s = _services.Settings;

            PlainPasteBox.Text = s.PlainPasteEnabled && s.PlainPasteHotkey.HasKey
                ? s.PlainPasteHotkey.ToString()
                : "（未启用）";
            PlainPasteEnabledCheck.IsChecked = s.PlainPasteEnabled;

            // 仅展示可配置的产品热键；Esc/Delete/方向/数字/Ctrl+C 为约定键，不在设置中列出
            PasteSelectedBox.Text = s.PasteSelectedHotkey.ToString();
            PasteSelectedPlainBox.Text = s.PasteSelectedPlainHotkey.ToString();
            TogglePinBox.Text = s.TogglePinHotkey.ToString();

            SelectThemeInBox(s.Theme);

            AutoStartCheck.IsChecked = s.AutoStart;
            AutoCheckUpdatesCheck.IsChecked = s.AutoCheckUpdates;
            TextOnlyCheck.IsChecked = s.TextOnlyCapture;
            ChannelText.Text = "当前渠道：" + UpdateService.ChannelLabel;
            MaxHistoryBox.Text = s.MaxHistoryItems.ToString();
            VersionText.Text = "v" + UpdateService.CurrentVersion;

            OcrEngineBox.SelectedIndex = s.OcrEngine switch
            {
                OcrEngineType.Ollama => 1,
                OcrEngineType.OpenAi => 2,
                _ => 0
            };

            OllamaBaseUrlBox.Text = s.OllamaBaseUrl;
            OllamaModelBox.Text = s.OllamaModel;
            OpenAiBaseUrlBox.Text = s.OpenAiBaseUrl;
            OpenAiModelBox.Text = s.OpenAiModel;
            OpenAiApiKeyBox.Password = s.OpenAiApiKey ?? string.Empty;
            OllamaPanel.Visibility = s.OcrEngine == OcrEngineType.Ollama ? Visibility.Visible : Visibility.Collapsed;
            OpenAiPanel.Visibility = s.OcrEngine == OcrEngineType.OpenAi ? Visibility.Visible : Visibility.Collapsed;
            OcrTestPanel.Visibility = s.OcrEngine is OcrEngineType.Ollama or OcrEngineType.OpenAi
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshSysClipboardStatus();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    /// <summary>
    /// 刷新 Windows 剪贴板历史接管状态与 UI 指示徽章。
    /// </summary>
    private void RefreshSysClipboardStatus()
    {
        bool sysEnabled = SystemClipboardService.IsClipboardHistoryEnabled();
        bool nativeRegistered = _services.Hotkey.IsWinVRegistered;

        if (sysEnabled)
        {
            SysClipboardStatusText.Text = "系统已开启 (可能冲突)";
            if (FindResource("Theme.Pin") is MediaBrush pinBrush)
            {
                SysClipboardStatusText.Foreground = pinBrush;
            }
            if (FindResource("Theme.Card") is MediaBrush cardBrush)
            {
                SysClipboardStatusBadge.Background = cardBrush;
            }
            ToggleSysClipboardButton.Content = "一键禁用";
            ToggleSysClipboardButton.ToolTip = "一键禁用 Windows 自带剪贴板历史，释放 Win+V 独占原生接管";
            SysClipboardHintText.Text = "Windows 自带剪贴板历史正在占用 Win+V。点击「一键禁用」即可彻底释放快捷键，实现 0 延迟原生接管。";
        }
        else
        {
            SysClipboardStatusText.Text = nativeRegistered ? "原生独占接管 (推荐)" : "系统历史已关闭";
            if (FindResource("Theme.Accent") is MediaBrush accentBrush)
            {
                SysClipboardStatusText.Foreground = accentBrush;
            }
            if (FindResource("Theme.AccentMuted") is MediaBrush accentMutedBrush)
            {
                SysClipboardStatusBadge.Background = accentMutedBrush;
            }
            ToggleSysClipboardButton.Content = "恢复系统";
            ToggleSysClipboardButton.ToolTip = "恢复开启 Windows 自带剪贴板历史记录";
            SysClipboardHintText.Text = "Windows 自带剪贴板历史已关闭，Win+V 已由 QuickClip 独占原生接管，无冲突风险。";
        }
    }

    /// <summary>
    /// 一键切换 Windows 剪贴板历史开启/禁用状态并重新应用热键。
    /// </summary>
    private void OnToggleSysClipboardClicked(object sender, RoutedEventArgs e)
    {
        bool current = SystemClipboardService.IsClipboardHistoryEnabled();
        bool target = !current;
        bool success = SystemClipboardService.SetClipboardHistoryEnabled(target);
        if (success)
        {
            // 重新尝试原生注册 Win+V
            _services.Hotkey.RefreshHotkeys();
            RefreshSysClipboardStatus();
            SetHotkeyHint(target ? "已恢复 Windows 自带剪贴板历史" : "已禁用 Windows 自带剪贴板历史，Win+V 已独占释放");
        }
        else
        {
            SetHotkeyHint("修改 Windows 剪贴板配置失败，请检查注册表写入权限");
        }
    }

    private void OnSettingsChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshUi();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
        {
            SetHotkeyHint($"正在设置「{DescribeTag(box.Tag)}」：请按下新的快捷键组合");
        }
    }

    /// <summary>仅在需要反馈时显示提示；勾选启用状态不再刷文案。</summary>
    private void SetHotkeyHint(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HotkeyHintText.Text = string.Empty;
            HotkeyHintText.Visibility = Visibility.Collapsed;
            return;
        }

        HotkeyHintText.Text = message;
        HotkeyHintText.Visibility = Visibility.Visible;
    }

    /// <summary>热键捕获：在输入框内按下组合键即应用。</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { Tag: string tag } box)
        {
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 仅按下修饰键本身时不提交
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            SetHotkeyHint("请继续按下主键（字母 / 数字 / 功能键）");
            return;
        }

        key = HotkeyBinding.NormalizeKey(key);
        var modifiers = Keyboard.Modifiers &
                        (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        var binding = new HotkeyBinding(modifiers, key);

        if (tag == "PlainPaste")
        {
            ApplyPlainPaste(binding);
            return;
        }

        if (!Enum.TryParse<PanelHotkeyAction>(tag, out var action))
        {
            return;
        }

        string? error = ValidatePanelHotkey(action, binding);
        if (error != null)
        {
            SetHotkeyHint(error);
            return;
        }

        _services.Settings.SetPanelHotkey(action, binding);
        SetHotkeyHint($"已应用「{DescribeTag(tag)}」：{binding}");
        RefreshUi();
    }

    private void ApplyPlainPaste(HotkeyBinding binding)
    {
        // Esc / Delete / Backspace（无修饰键）：禁用全局热键
        if (binding.Modifiers == ModifierKeys.None &&
            binding.Key is Key.Escape or Key.Delete or Key.Back)
        {
            _services.Settings.SetPlainPaste(HotkeyBinding.PlainPasteDefault, enabled: false);
            SetHotkeyHint("已禁用（Esc / Delete / Backspace）；也可取消上方勾选");
            RefreshUi();
            return;
        }

        string? error = ValidatePlainPaste(binding);
        if (error != null)
        {
            SetHotkeyHint(error);
            return;
        }

        _services.Settings.SetPlainPaste(binding, enabled: true);
        SetHotkeyHint($"已应用：{binding}");
        RefreshUi();
    }

    private static string? ValidatePlainPaste(HotkeyBinding binding)
    {
        if (binding.Modifiers == ModifierKeys.None)
        {
            return "全局热键请至少搭配一个修饰键（Ctrl / Alt / Shift / Win）";
        }

        if (binding == HotkeyBinding.WinV)
        {
            return "Win + V 为系统保留（唤起面板），不可占用";
        }

        if (binding.Modifiers == ModifierKeys.Control && binding.Key is Key.C or Key.X or Key.V)
        {
            return "Ctrl + C / X / V 是系统常用编辑键，请更换组合";
        }

        return null;
    }

    private string? ValidatePanelHotkey(PanelHotkeyAction action, HotkeyBinding binding)
    {
        if (!binding.HasKey)
        {
            return "请按下有效主键";
        }

        if (binding == HotkeyBinding.WinV)
        {
            return "Win + V 为系统保留，不可占用";
        }

        // 数字键 1~9 留给快速粘贴
        if (binding.Modifiers == ModifierKeys.None &&
            binding.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            return "数字键 1~9 固定用于快速粘贴，请更换组合";
        }

        // 与其它面板快捷键冲突检测
        foreach (PanelHotkeyAction other in Enum.GetValues<PanelHotkeyAction>())
        {
            if (other == action)
            {
                continue;
            }

            if (_services.Settings.GetPanelHotkey(other) == binding)
            {
                return $"与「{DescribeAction(other)}」冲突（当前为 {binding}），请更换";
            }
        }

        // 与已启用的全局纯文本粘贴冲突
        if (_services.Settings.PlainPasteEnabled &&
            _services.Settings.PlainPasteHotkey == binding)
        {
            return $"与全局纯文本粘贴冲突（{binding}），请更换";
        }

        return null;
    }

    private void OnResetHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (tag == "PlainPaste")
        {
            _services.Settings.SetPlainPaste(HotkeyBinding.PlainPasteDefault, enabled: true);
            SetHotkeyHint("已恢复默认：Ctrl + Shift + V");
            RefreshUi();
            return;
        }

        if (Enum.TryParse<PanelHotkeyAction>(tag, out var action))
        {
            var def = SettingsService.GetPanelHotkeyDefault(action);
            // 恢复默认时若与其它项冲突（极少见），仍强制写回默认
            _services.Settings.SetPanelHotkey(action, def);
            SetHotkeyHint($"已恢复「{DescribeAction(action)}」默认：{def}");
            RefreshUi();
        }
    }

    private void OnPlainPasteEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        if (PlainPasteEnabledCheck.IsChecked == _services.Settings.PlainPasteEnabled)
        {
            return;
        }

        // 启用状态只以勾选框为准，不再刷「已启用/已禁用」提示文案
        _services.Settings.SetPlainPaste(
            _services.Settings.PlainPasteHotkey,
            PlainPasteEnabledCheck.IsChecked == true);
        SetHotkeyHint(null);
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        if (AutoStartCheck.IsChecked == _services.Settings.AutoStart)
        {
            return;
        }

        _services.Settings.SetAutoStart(AutoStartCheck.IsChecked == true);
    }

    private void OnAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        bool enabled = AutoCheckUpdatesCheck.IsChecked == true;
        if (enabled == _services.Settings.AutoCheckUpdates)
        {
            return;
        }

        _services.Settings.SetAutoCheckUpdates(enabled);
        if (enabled)
        {
            _ = CheckAndDownloadFromSettingsAsync();
        }
    }

    private void OnPendingUpdateChanged(PendingUpdate? _)
    {
        Dispatcher.BeginInvoke(RefreshUpdatePanel);
    }

    private void OnDownloadFailedChanged(DownloadFailedInfo? _)
    {
        Dispatcher.BeginInvoke(RefreshUpdatePanel);
    }

    private void RefreshUpdatePanel()
    {
        var pending = _services.Update.Pending;
        var failed = _services.Update.DownloadFailed;

        bool ready = pending != null && File.Exists(pending.LocalPath);
        if (ready)
        {
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallUpdateButton.Content = UpdateService.ApplyActionLabel;
            BrowserDownloadButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"新版本 {pending!.TagName} 已下载就绪，点击「立即更新」启动安装程序。";
            if (FindResource("Theme.Accent") is MediaBrush accentBrush)
            {
                UpdateStatusText.Foreground = accentBrush;
            }
            return;
        }

        InstallUpdateButton.Visibility = Visibility.Collapsed;

        if (failed != null)
        {
            BrowserDownloadButton.Visibility = Visibility.Visible;
            UpdateStatusText.Text = $"⚠️ 发现新版本 {failed.TagName}，自动下载失败。可点击右侧按钮直接在浏览器中下载安装包。";
            if (FindResource("Theme.Pin") is MediaBrush pinBrush)
            {
                UpdateStatusText.Foreground = pinBrush;
            }
            return;
        }

        BrowserDownloadButton.Visibility = Visibility.Collapsed;
        if (FindResource("Theme.TextSecondary") is MediaBrush textSecondaryBrush)
        {
            UpdateStatusText.Foreground = textSecondaryBrush;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !_themeBoxReady || !IsLoaded)
        {
            return;
        }

        if (ThemeBox.SelectedItem is not ThemeOption opt)
        {
            return;
        }

        if (opt.Id == _services.Settings.Theme)
        {
            return;
        }

        _services.Settings.SetTheme(opt.Id);
    }

    private void SelectThemeInBox(AppTheme theme)
    {
        for (int i = 0; i < ThemeBox.Items.Count; i++)
        {
            if (ThemeBox.Items[i] is ThemeOption opt && opt.Id == theme)
            {
                ThemeBox.SelectedIndex = i;
                return;
            }
        }

        ThemeBox.SelectedIndex = 0;
    }

    private void OnTextOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _services.Settings.SetTextOnlyCapture(TextOnlyCheck.IsChecked == true);
    }

    private void OnMaxHistoryLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (!int.TryParse(MaxHistoryBox.Text.Trim(), out int n))
        {
            MaxHistoryBox.Text = _services.Settings.MaxHistoryItems.ToString();
            return;
        }

        _services.Settings.SetMaxHistoryItems(n);
        MaxHistoryBox.Text = _services.Settings.MaxHistoryItems.ToString();
        // 立即按新上限裁剪
        _ = TrimHistoryAfterSettingChangeAsync();
    }

    private async System.Threading.Tasks.Task TrimHistoryAfterSettingChangeAsync()
    {
        try
        {
            var trimmed = await _services.Database.TrimToMaxItemsAsync(_services.Settings.MaxHistoryItems);
            foreach (var (_, path) in trimmed)
            {
                ThumbnailCache.RemoveByPath(path);
            }

            if (_services.MainWindow?.ViewModel != null)
            {
                await _services.MainWindow.ViewModel.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("按条数裁剪历史失败", ex);
        }
    }

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _services.Paths.BaseDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DebugLog.LogException("打开数据目录失败", ex);
        }
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e) =>
        await CheckAndDownloadFromSettingsAsync();

    private async Task CheckAndDownloadFromSettingsAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var result = await _services.Update.CheckAndDownloadAsync(interactive: true);
            UpdateStatusText.Text = result.Message ?? string.Empty;
            RefreshUpdatePanel();
        }
        finally
        {
            _busy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_services.Update.TryApplyPending(out string message, out bool shouldExit))
        {
            UpdateStatusText.Text = message;
            if (shouldExit)
            {
                _services.MainWindow?.RequestExit();
            }

            return;
        }

        UpdateStatusText.Text = message;
        RefreshUpdatePanel();
    }

    private void OnBrowserDownloadClicked(object sender, RoutedEventArgs e)
    {
        UpdateService.OpenUrlInBrowser(_services.Update.DownloadFailed?.DownloadUrl);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ---------- OCR ----------

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
        {
            return;
        }

        var engine = OcrEngineBox.SelectedIndex switch
        {
            1 => OcrEngineType.Ollama,
            2 => OcrEngineType.OpenAi,
            _ => OcrEngineType.System
        };

        OllamaPanel.Visibility = engine == OcrEngineType.Ollama ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = engine == OcrEngineType.OpenAi ? Visibility.Visible : Visibility.Collapsed;
        OcrTestPanel.Visibility = engine is OcrEngineType.Ollama or OcrEngineType.OpenAi
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_services.Settings.OcrEngine != engine)
        {
            _services.Settings.SetOcrEngine(engine);
        }

        // 引擎面板显隐会改变内容高度，布局后再按有限视口重算滚动
        Dispatcher.BeginInvoke(RefreshScrollExtent, DispatcherPriority.Loaded);
    }

    private void OnOllamaConfigLostFocus(object sender, RoutedEventArgs e)
    {
        _services.Settings.SetOllamaConfig(OllamaBaseUrlBox.Text, OllamaModelBox.Text);
    }

    private void OnOpenAiConfigLostFocus(object sender, RoutedEventArgs e)
    {
        _services.Settings.SetOpenAiConfig(
            OpenAiBaseUrlBox.Text,
            OpenAiModelBox.Text,
            OpenAiApiKeyBox.Password);
    }

    private async void OnOcrTestClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        PersistOcrForm();
        _busy = true;
        OcrTestButton.IsEnabled = false;
        OcrTestStatus.Text = "正在测试 " + _services.Ocr.ConfiguredEngineTitle + "…";
        try
        {
            OcrTestStatus.Text = await _services.Ocr.ProbeConfiguredEngineAsync();
        }
        finally
        {
            _busy = false;
            OcrTestButton.IsEnabled = true;
        }
    }

    private void PersistOcrForm()
    {
        if (OcrEngineBox.SelectedIndex == 1)
        {
            _services.Settings.SetOllamaConfig(OllamaBaseUrlBox.Text, OllamaModelBox.Text);
            return;
        }

        if (OcrEngineBox.SelectedIndex == 2)
        {
            _services.Settings.SetOpenAiConfig(
                OpenAiBaseUrlBox.Text,
                OpenAiModelBox.Text,
                OpenAiApiKeyBox.Password);
        }
    }

    // ---------- 文案 ----------

    private static string DescribeTag(object? tag) => tag switch
    {
        "PlainPaste" => "全局纯文本粘贴",
        "PasteSelected" => "粘贴选中项",
        "PasteSelectedPlain" => "纯文本粘贴选中项",
        "CopySelected" => "复制选中项",
        "TogglePin" => "窗口置顶",
        "DeleteSelected" => "删除选中项",
        "HidePanel" => "隐藏面板",
        "MoveUp" => "选中上一项",
        "MoveDown" => "选中下一项",
        _ => "快捷键"
    };

    private static string DescribeAction(PanelHotkeyAction action) => action switch
    {
        PanelHotkeyAction.PasteSelected => "粘贴选中项",
        PanelHotkeyAction.PasteSelectedPlain => "纯文本粘贴选中项",
        PanelHotkeyAction.CopySelected => "复制选中项",
        PanelHotkeyAction.TogglePin => "窗口置顶",
        PanelHotkeyAction.DeleteSelected => "删除选中项",
        PanelHotkeyAction.HidePanel => "隐藏面板",
        PanelHotkeyAction.MoveUp => "选中上一项",
        PanelHotkeyAction.MoveDown => "选中下一项",
        _ => action.ToString()
    };
}
