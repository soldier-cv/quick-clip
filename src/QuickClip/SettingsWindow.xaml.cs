using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Models;
using QuickClip.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
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
    private IReadOnlyList<string> _fontFamilies = Array.Empty<string>();
    private bool _suppressFontFilter;

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
        FontFamilyBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnFontFamilyTextChanged), true);
        FillThemeBox();

        RefreshUi();
        RefreshUpdatePanel();
        _services.Settings.Changed += OnSettingsChanged;
        _services.Update.PendingChanged += OnPendingUpdateChanged;
        _services.Update.DownloadFailedChanged += OnDownloadFailedChanged;
        _services.Update.ActivityChanged += OnUpdateActivityChanged;
        _services.OcrPacks.DownloadProgress += OnOcrDownloadProgress;
        _services.OcrPacks.PacksChanged += OnOcrPacksChanged;
        ThemeService.Changed += OnThemeServiceChanged;
        Loaded += OnSettingsWindowLoaded;
        Closed += (_, _) =>
        {
            _services.Settings.Changed -= OnSettingsChanged;
            _services.Update.PendingChanged -= OnPendingUpdateChanged;
            _services.Update.DownloadFailedChanged -= OnDownloadFailedChanged;
            _services.Update.ActivityChanged -= OnUpdateActivityChanged;
            _services.OcrPacks.DownloadProgress -= OnOcrDownloadProgress;
            _services.OcrPacks.PacksChanged -= OnOcrPacksChanged;
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
        RefreshLocalOcrPanel();
    }

    private void RefreshUi()
    {
        _suppressUiEvents = true;
        try
        {
            var s = _services.Settings;

            PlainPasteEnabledCheck.IsChecked = s.PlainPasteEnabled;

            // 仅展示可配置的产品热键；Esc/Delete/方向/数字/Ctrl+C 为约定键，不在设置中列出
            PasteSelectedBox.Text = s.PasteSelectedHotkey.ToString();
            PasteSelectedPlainBox.Text = s.PasteSelectedPlainHotkey.ToString();
            TogglePinBox.Text = s.TogglePinHotkey.ToString();
            StartStackBox.Text = s.StartStackHotkey.ToString();

            SelectThemeInBox(s.Theme);
            FillFontFamilyBox(s.UiFontFamily);

            AutoStartCheck.IsChecked = s.AutoStart;
            AutoCheckUpdatesCheck.IsChecked = s.AutoCheckUpdates;
            TextOnlyCheck.IsChecked = s.TextOnlyCapture;
            ChannelText.Text = "当前渠道：" + UpdateService.ChannelLabel;
            MaxHistoryBox.Text = s.MaxHistoryItems.ToString();
            VersionText.Text = "v" + UpdateService.CurrentVersion;

            OcrEngineBox.SelectedIndex = s.OcrEngine switch
            {
                OcrEngineType.Local => 1,
                OcrEngineType.VisionApi => 2,
                _ => 0
            };

            SetModelBoxItemsSource(VisionApiModelBox, null, s.OcrVisionModel);
            VisionApiPromptBox.Text = s.OcrPrompt;
            OcrCustomDirBox.Text = s.OcrCustomDir;

            TranslationEngineBox.SelectedIndex = s.TranslationEngine switch
            {
                TranslationEngineType.Google => 1,
                TranslationEngineType.Ai => 2,
                _ => 0
            };
            SetModelBoxItemsSource(TranslationModelBox, null, s.TranslationModel);
            TranslationPromptBox.Text = s.TranslationPrompt;

            ApplyOcrEnginePanels(s.OcrEngine);
            ApplyTranslationEnginePanels(s.TranslationEngine);
            RefreshLocalOcrPanel();
            RefreshAiProfileBoxes();

            RefreshSysClipboardStatus();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    /// <summary>
    /// 刷新 Windows 剪贴板接管状态与 UI 指示徽章。
    /// </summary>
    private void RefreshSysClipboardStatus()
    {
        bool takeOver = _services.Settings.TakeOverSystemClipboard;
        bool sysEnabled = SystemClipboardService.IsClipboardHistoryEnabled();
        bool nativeRegistered = _services.Hotkey.IsWinVRegistered;

        SysClipboardToggleButton.Content = takeOver ? "关闭接管" : "启用接管";

        if (!takeOver)
        {
            SysClipboardStatusText.Text = sysEnabled ? "未接管（系统历史开启）" : "未接管（系统历史关闭）";
            if (FindResource("Theme.TextSecondary") is MediaBrush idleBrush)
            {
                SysClipboardStatusText.Foreground = idleBrush;
            }

            if (FindResource("Theme.Card") is MediaBrush idleCardBrush)
            {
                SysClipboardStatusBadge.Background = idleCardBrush;
            }

            SysClipboardHintText.Text =
                "已关闭接管，Windows 自带剪贴板历史与 Win+V 按接管前状态还原。" +
                "QuickClip 仍由键盘钩子接管 Win+V，但可能与系统剪贴板历史同时响应。";
            return;
        }

        if (sysEnabled)
        {
            SysClipboardStatusText.Text = "系统已开启（可能冲突）";
            if (FindResource("Theme.Pin") is MediaBrush pinBrush)
            {
                SysClipboardStatusText.Foreground = pinBrush;
            }
            if (FindResource("Theme.Card") is MediaBrush cardBrush)
            {
                SysClipboardStatusBadge.Background = cardBrush;
            }
            SysClipboardHintText.Text = "Windows 剪贴板历史正在占用 Win+V，QuickClip 已通过键盘钩子与窗口守卫兜底接管。";
        }
        else
        {
            SysClipboardStatusText.Text = nativeRegistered ? "原生独占接管（推荐）" : "系统历史已关闭";
            if (FindResource("Theme.Accent") is MediaBrush accentBrush)
            {
                SysClipboardStatusText.Foreground = accentBrush;
            }
            if (FindResource("Theme.AccentMuted") is MediaBrush accentMutedBrush)
            {
                SysClipboardStatusBadge.Background = accentMutedBrush;
            }
            SysClipboardHintText.Text = "系统剪贴板历史已关闭，Win+V 已由 QuickClip 独占接管，无冲突风险。";
        }
    }

    /// <summary>
    /// 切换「接管系统剪贴板」：开启时快照并接管，关闭时按接管前快照精确还原系统状态。
    /// </summary>
    private void OnToggleSysClipboardClicked(object sender, RoutedEventArgs e)
    {
        bool target = !_services.Settings.TakeOverSystemClipboard;
        try
        {
            _services.SetSystemClipboardTakeOver(target);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("切换系统剪贴板接管失败", ex);
            SetHotkeyHint("修改系统剪贴板配置失败，请检查注册表写入权限");
            return;
        }

        RefreshSysClipboardStatus();
        SetHotkeyHint(target
            ? "已启用接管：Windows 剪贴板历史已关闭，Win+V 由 QuickClip 独占"
            : "已关闭接管：Windows 剪贴板历史与 Win+V 已按接管前状态还原");
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

        // 与已启用的全局纯文本粘贴（固定 Ctrl+Shift+V）冲突
        if (_services.Settings.PlainPasteEnabled &&
            HotkeyBinding.PlainPasteDefault == binding)
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

        // 启用状态只以勾选框为准；组合固定为 Ctrl+Shift+V
        _services.Settings.SetPlainPasteEnabled(PlainPasteEnabledCheck.IsChecked == true);
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

    private void OnUpdateActivityChanged(UpdateActivity activity)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshUpdatePanel();
            if (activity.Phase != UpdatePhase.Checking)
            {
                _busy = false;
                CheckUpdateButton.IsEnabled = true;
            }
        });
    }

    private void RefreshUpdatePanel()
    {
        var pending = _services.Update.Pending;
        var failed = _services.Update.DownloadFailed;
        var activity = _services.Update.Activity;

        bool ready = pending != null && File.Exists(pending.LocalPath);
        if (ready)
        {
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallUpdateButton.Content = UpdateService.ApplyActionLabel;
            BrowserDownloadButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"新版本 {pending!.TagName} 已下载就绪。点击「立即更新」将退出并静默安装；下次手动启动也会自动安装。";
            if (FindResource("Theme.Accent") is MediaBrush accentBrush)
            {
                UpdateStatusText.Foreground = accentBrush;
            }
            return;
        }

        InstallUpdateButton.Visibility = Visibility.Collapsed;

        if (activity.Phase == UpdatePhase.Checking)
        {
            BrowserDownloadButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "正在检查更新…";
            return;
        }

        if (activity.Phase == UpdatePhase.Downloading)
        {
            BrowserDownloadButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = activity.Message;
            if (FindResource("Theme.Accent") is MediaBrush accentBrush)
            {
                UpdateStatusText.Foreground = accentBrush;
            }
            return;
        }

        if (failed != null || activity.Phase == UpdatePhase.Failed)
        {
            BrowserDownloadButton.Visibility = Visibility.Visible;
            UpdateStatusText.Text = failed != null
                ? $"发现新版本 {failed.TagName}，自动下载失败。可点击右侧按钮直接在浏览器中下载安装包。"
                : activity.Message;
            if (FindResource("Theme.Pin") is MediaBrush pinBrush)
            {
                UpdateStatusText.Foreground = pinBrush;
            }
            return;
        }

        BrowserDownloadButton.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(activity.Message) && activity.Phase is UpdatePhase.UpToDate or UpdatePhase.Idle)
        {
            UpdateStatusText.Text = activity.Message;
        }

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

    private void FillFontFamilyBox(string current)
    {
        if (_fontFamilies == null || FontFamilyBox.ItemsSource == null)
        {
            _fontFamilies = AppFontService.ListInstalledFamilies();
            FontFamilyBox.ItemsSource = _fontFamilies;
            FilterFontFamilies(string.Empty);
        }

        string selected = string.IsNullOrWhiteSpace(current)
            ? AppFontService.DefaultDisplayName
            : current;
        if (!_fontFamilies.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            selected = AppFontService.DefaultDisplayName;
        }

        if (!string.Equals(FontFamilyBox.SelectedItem as string, selected, StringComparison.OrdinalIgnoreCase))
        {
            _suppressUiEvents = true;
            _suppressFontFilter = true;
            try
            {
                FontFamilyBox.SelectedItem = selected;
                FontFamilyBox.Text = selected;
            }
            finally
            {
                _suppressUiEvents = false;
                _suppressFontFilter = false;
            }
        }
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _suppressFontFilter || !IsLoaded)
        {
            return;
        }

        if (FontFamilyBox.SelectedItem is not string name)
        {
            return;
        }

        _services.Settings.SetUiFontFamily(name);
    }

    private void OnFontFamilyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || _suppressFontFilter || !IsLoaded)
        {
            return;
        }

        if (!FontFamilyBox.IsKeyboardFocusWithin)
        {
            return;
        }

        string query = FontFamilyBox.Text ?? string.Empty;
        int caret = GetFontFamilyCaretIndex();
        if (FontFamilyBox.SelectedItem is string selected &&
            string.Equals(selected, query, StringComparison.OrdinalIgnoreCase))
        {
            FilterFontFamilies(string.Empty, caret);
            return;
        }

        if (!FontFamilyBox.IsDropDownOpen)
        {
            FontFamilyBox.IsDropDownOpen = true;
        }

        FilterFontFamilies(query, caret);
    }

    private void OnFontFamilyDropDownClosed(object sender, EventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
        {
            return;
        }

        CommitOrRevertFontFamilyText();
    }

    private void OnFontFamilyPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        string? name = FontFamilyBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(name) && FontFamilyBox.Items.Count > 0)
        {
            name = FontFamilyBox.Items[0] as string;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        ApplyFontFamilySelection(name);
        FontFamilyBox.IsDropDownOpen = false;
        e.Handled = true;
    }

    private void OnFontFamilyLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded || FontFamilyBox.IsDropDownOpen)
        {
            return;
        }

        CommitOrRevertFontFamilyText();
    }

    private void CommitOrRevertFontFamilyText()
    {
        string typed = (FontFamilyBox.Text ?? string.Empty).Trim();
        string? match = _fontFamilies.FirstOrDefault(n =>
            string.Equals(n, typed, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            ApplyFontFamilySelection(match);
            return;
        }

        string current = string.IsNullOrWhiteSpace(_services.Settings.UiFontFamily)
            ? AppFontService.DefaultDisplayName
            : _services.Settings.UiFontFamily;
        FilterFontFamilies(string.Empty);
        FontFamilyBox.SelectedItem = current;
        FontFamilyBox.Text = current;
    }

    private void ApplyFontFamilySelection(string name)
    {
        FilterFontFamilies(string.Empty);
        FontFamilyBox.SelectedItem = name;
        FontFamilyBox.Text = name;
        if (!_suppressUiEvents && IsLoaded)
        {
            _services.Settings.SetUiFontFamily(name);
        }
    }

    private void FilterFontFamilies(string query, int? caret = null)
    {
        if (FontFamilyBox.ItemsSource == null)
        {
            return;
        }

        _suppressFontFilter = true;
        try
        {
            if (CollectionViewSource.GetDefaultView(FontFamilyBox.ItemsSource) is not ICollectionView view)
            {
                return;
            }

            string text = FontFamilyBox.Text ?? string.Empty;
            int restoreCaret = caret ?? GetFontFamilyCaretIndex();
            string q = query.Trim();
            if (string.IsNullOrEmpty(q))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj => obj is string name && FontNameMatches(name, q);
            }

            view.Refresh();
            FontFamilyBox.Text = text;
            SetFontFamilyCaretIndex(restoreCaret);
        }
        finally
        {
            _suppressFontFilter = false;
        }
    }

    private TextBox? GetFontFamilyEditBox()
    {
        return FontFamilyBox.Template?.FindName("PART_EditableTextBox", FontFamilyBox) as TextBox;
    }

    private int GetFontFamilyCaretIndex()
    {
        return GetFontFamilyEditBox()?.CaretIndex ?? (FontFamilyBox.Text?.Length ?? 0);
    }

    private void SetFontFamilyCaretIndex(int caret)
    {
        if (GetFontFamilyEditBox() is not TextBox box)
        {
            return;
        }

        int max = box.Text?.Length ?? 0;
        box.CaretIndex = Math.Clamp(caret, 0, max);
    }

    private static bool FontNameMatches(string name, string query)
    {
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string initials = PinyinUtil.GetInitials(name);
        return initials.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnTextOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _services.Settings.SetTextOnlyCapture(TextOnlyCheck.IsChecked == true);
    }

    private async void OnMaxHistoryLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (!int.TryParse(MaxHistoryBox.Text.Trim(), out int n))
        {
            MaxHistoryBox.Text = _services.Settings.MaxHistoryItems.ToString();
            return;
        }

        // 调小上限会立即永久删除最旧的非置顶记录：先确认，避免输入框失焦/误触就删库
        int clamped = SettingsService.ClampMaxHistory(n);
        if (clamped < _services.Settings.MaxHistoryItems)
        {
            int count = await _services.Database.CountAsync();
            if (clamped < count)
            {
                var confirm = System.Windows.MessageBox.Show(
                    this,
                    $"当前已有 {count} 条历史。\n新上限 {clamped} 会立即永久删除最旧的 {count - clamped} 条非置顶记录（置顶保留）。\n\n确定继续？",
                    "调整历史条数",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);

                if (confirm != System.Windows.MessageBoxResult.Yes)
                {
                    MaxHistoryBox.Text = _services.Settings.MaxHistoryItems.ToString();
                    return;
                }
            }
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
            RefreshUpdatePanel();
            return;
        }

        _busy = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            await _services.Update.CheckAndDownloadAsync(interactive: true);
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
            1 => OcrEngineType.Local,
            2 => OcrEngineType.VisionApi,
            _ => OcrEngineType.System
        };

        ApplyOcrEnginePanels(engine);

        if (_services.Settings.OcrEngine != engine)
        {
            _services.Settings.SetOcrEngine(engine);
        }

        if (engine == OcrEngineType.Local)
        {
            RefreshLocalOcrPanel();
        }

        // 引擎面板显隐会改变内容高度，布局后再按有限视口重算滚动
        Dispatcher.BeginInvoke(RefreshScrollExtent, DispatcherPriority.Loaded);
    }

    // ---------- 模型配置（多组配置项管理） ----------

    private void RefreshAiProfileBoxes()
    {
        var profiles = _services.Settings.AiProfiles;
        if (profiles.Count == 0) return;

        bool prevSuppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            // 1. AiProfileBox
            string? selectedAiProfileId = (AiProfileBox.SelectedItem as AiProfile)?.Id ?? profiles[0].Id;
            AiProfileBox.ItemsSource = null;
            AiProfileBox.ItemsSource = profiles;
            var activeAiProfile = profiles.FirstOrDefault(p => p.Id == selectedAiProfileId) ?? profiles[0];
            AiProfileBox.SelectedItem = activeAiProfile;
            DeleteAiProfileButton.IsEnabled = profiles.Count > 1;

            // 2. OcrProfileBox
            string? selectedOcrId = _services.Settings.OcrProfileId ?? profiles[0].Id;
            OcrProfileBox.ItemsSource = null;
            OcrProfileBox.ItemsSource = profiles;
            OcrProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == selectedOcrId) ?? profiles[0];

            // 3. TranslationProfileBox
            string? selectedTransId = _services.Settings.TranslationProfileId ?? profiles[0].Id;
            TranslationProfileBox.ItemsSource = null;
            TranslationProfileBox.ItemsSource = profiles;
            TranslationProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == selectedTransId) ?? profiles[0];

            // 4. 加载当前选中的配置项详情
            LoadAiProfileToInputs(activeAiProfile);

            // 5. 联动刷新 OCR 与 翻译的模型下拉项
            RefreshOcrModelsFromSelectedProfile();
            RefreshTranslationModelsFromSelectedProfile();
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
        }
    }

    private void LoadAiProfileToInputs(AiProfile profile)
    {
        AiProfileNameBox.Text = profile.Name;
        AiApiUrlBox.Text = profile.ApiUrl;
        AiApiKeyBox.Password = profile.ApiKey ?? string.Empty;
    }

    private void SetModelBoxItemsSource(ComboBox box, IEnumerable<string>? models, string? currentModel)
    {
        var list = models?.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList() ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(currentModel) && !list.Contains(currentModel))
        {
            list.Insert(0, currentModel);
        }
        box.ItemsSource = list;
        box.SelectedItem = currentModel;
    }

    private void RefreshOcrModelsFromSelectedProfile()
    {
        if (OcrProfileBox.SelectedItem is AiProfile profile)
        {
            var models = profile.CachedModels.Count > 0
                ? profile.CachedModels
                : _services.Settings.GetCachedModelsForUrl(profile.ApiUrl);
            SetModelBoxItemsSource(VisionApiModelBox, models, _services.Settings.OcrVisionModel);
        }
    }

    private void RefreshTranslationModelsFromSelectedProfile()
    {
        if (TranslationProfileBox.SelectedItem is AiProfile profile)
        {
            var models = profile.CachedModels.Count > 0
                ? profile.CachedModels
                : _services.Settings.GetCachedModelsForUrl(profile.ApiUrl);
            SetModelBoxItemsSource(TranslationModelBox, models, _services.Settings.TranslationModel);
        }
    }

    private void OnAiProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (AiProfileBox.SelectedItem is AiProfile profile)
        {
            LoadAiProfileToInputs(profile);
            AiTestStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAddAiProfileClicked(object sender, RoutedEventArgs e)
    {
        int count = _services.Settings.AiProfiles.Count + 1;
        var newProfile = new AiProfile
        {
            Name = $"配置 {count}",
            ApiUrl = "https://api.openai.com/v1/chat/completions"
        };
        _services.Settings.AddAiProfile(newProfile);
        RefreshAiProfileBoxes();
        AiProfileBox.SelectedItem = newProfile;
        AiProfileNameBox.Focus();
        AiProfileNameBox.SelectAll();
    }

    private void OnDeleteAiProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_services.Settings.AiProfiles.Count <= 1) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        _services.Settings.DeleteAiProfile(current.Id);
        RefreshAiProfileBoxes();
    }

    private void OnAiProfileNameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        string newName = AiProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            newName = "未命名配置";
        }

        if (current.Name != newName)
        {
            current.Name = newName;
            _services.Settings.UpdateAiProfile(current);
            AiProfileBox.Items.Refresh();
            OcrProfileBox.Items.Refresh();
            TranslationProfileBox.Items.Refresh();
        }
    }

    private void OnAiProfileNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        string newName = AiProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            newName = "未命名配置";
            AiProfileNameBox.Text = newName;
        }

        if (current.Name != newName)
        {
            current.Name = newName;
            _services.Settings.UpdateAiProfile(current);
            RefreshAiProfileBoxes();
        }
    }

    private void OnAiConfigLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        current.ApiUrl = string.IsNullOrWhiteSpace(AiApiUrlBox.Text)
            ? "https://api.openai.com/v1/chat/completions"
            : SettingsService.MigrateVisionEndpoint(AiApiUrlBox.Text);
        current.ApiKey = string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? null : AiApiKeyBox.Password.Trim();
        _services.Settings.UpdateAiProfile(current);
    }

    private void OnAiApiUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        // 保持界面顺畅，不强制触发全局刷新
    }

    private void OnFillSampleUrlClicked(object sender, RoutedEventArgs e)
    {
        ApplySampleUrl("https://api.openai.com/v1/chat/completions", "api.openai.com");
    }

    private void ApplySampleUrl(string url, string selectToken)
    {
        AiApiUrlBox.Text = url;
        AiApiUrlBox.Focus();
        int idx = url.IndexOf(selectToken, StringComparison.Ordinal);
        if (idx >= 0)
        {
            AiApiUrlBox.Select(idx, selectToken.Length);
        }
        else
        {
            AiApiUrlBox.CaretIndex = url.Length;
        }

        if (AiProfileBox.SelectedItem is AiProfile current)
        {
            current.ApiUrl = url;
            current.ApiKey = string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? null : AiApiKeyBox.Password.Trim();
            _services.Settings.UpdateAiProfile(current);
        }
    }

    private async void OnFetchModelsClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        string url = AiApiUrlBox.Text.Trim();
        string key = AiApiKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            AiTestStatus.Text = "请先在上方输入接口地址。";
            AiTestStatus.Visibility = Visibility.Visible;
            return;
        }

        _busy = true;
        FetchModelsButton.IsEnabled = false;
        AiTestStatus.Visibility = Visibility.Visible;
        AiTestStatus.Text = "正在从接口拉取模型列表…";

        try
        {
            var models = await _services.Ocr.FetchAvailableModelsAsync(url, key);
            current.ApiUrl = url;
            current.ApiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            current.CachedModels = models.ToList();
            _services.Settings.UpdateAiProfile(current);
            _services.Settings.SetCachedModelsForUrl(url, models);

            RefreshOcrModelsFromSelectedProfile();
            RefreshTranslationModelsFromSelectedProfile();

            AiTestStatus.Text = $"已获取 {models.Count} 个模型";
        }
        catch (Exception ex)
        {
            AiTestStatus.Text = "获取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            FetchModelsButton.IsEnabled = true;
        }
    }

    private async void OnAiTestClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (AiProfileBox.SelectedItem is not AiProfile current) return;

        current.ApiUrl = string.IsNullOrWhiteSpace(AiApiUrlBox.Text)
            ? "https://api.openai.com/v1/chat/completions"
            : SettingsService.MigrateVisionEndpoint(AiApiUrlBox.Text);
        current.ApiKey = string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? null : AiApiKeyBox.Password.Trim();
        _services.Settings.UpdateAiProfile(current);

        _busy = true;
        AiTestButton.IsEnabled = false;
        AiTestStatus.Visibility = Visibility.Visible;
        AiTestStatus.Text = "正在连接…";
        try
        {
            AiTestStatus.Text = await _services.Ocr.ProbeConfiguredEngineAsync();
        }
        finally
        {
            _busy = false;
            AiTestButton.IsEnabled = true;
        }
    }

    private void OnOcrProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (OcrProfileBox.SelectedItem is AiProfile profile)
        {
            _services.Settings.SetOcrProfileId(profile.Id);
            RefreshOcrModelsFromSelectedProfile();
        }
    }

    private async void OnOcrFetchModelsClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var profile = _services.Settings.GetOcrProfile();
        string url = profile.ApiUrl.Trim();
        string key = profile.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;

        _busy = true;
        OcrFetchModelsButton.IsEnabled = false;
        try
        {
            var models = await _services.Ocr.FetchAvailableModelsAsync(url, key);
            profile.CachedModels = models.ToList();
            _services.Settings.UpdateAiProfile(profile);
            _services.Settings.SetCachedModelsForUrl(url, models);

            string currentOcrModel = VisionApiModelBox.SelectedItem as string ?? _services.Settings.OcrVisionModel;
            SetModelBoxItemsSource(VisionApiModelBox, models, currentOcrModel);
            if (VisionApiModelBox.SelectedItem == null && models.Count > 0)
            {
                VisionApiModelBox.SelectedIndex = 0;
            }
            if (VisionApiModelBox.SelectedItem is string selectedOcrModel)
            {
                _services.Settings.SetOcrVisionModel(selectedOcrModel);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("OCR 获取模型失败", ex);
        }
        finally
        {
            _busy = false;
            OcrFetchModelsButton.IsEnabled = true;
        }
    }

    private void OnTranslationProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (TranslationProfileBox.SelectedItem is AiProfile profile)
        {
            _services.Settings.SetTranslationProfileId(profile.Id);
            RefreshTranslationModelsFromSelectedProfile();
        }
    }

    private async void OnTranslationFetchModelsClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var profile = _services.Settings.GetTranslationProfile();
        string url = profile.ApiUrl.Trim();
        string key = profile.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;

        _busy = true;
        TranslationFetchModelsButton.IsEnabled = false;
        try
        {
            var models = await _services.Ocr.FetchAvailableModelsAsync(url, key);
            profile.CachedModels = models.ToList();
            _services.Settings.UpdateAiProfile(profile);
            _services.Settings.SetCachedModelsForUrl(url, models);

            string currentTransModel = TranslationModelBox.SelectedItem as string ?? _services.Settings.TranslationModel;
            SetModelBoxItemsSource(TranslationModelBox, models, currentTransModel);
            if (TranslationModelBox.SelectedItem == null && models.Count > 0)
            {
                TranslationModelBox.SelectedIndex = 0;
            }
            if (TranslationModelBox.SelectedItem is string selectedTransModel)
            {
                _services.Settings.SetTranslationModel(selectedTransModel);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("翻译获取模型失败", ex);
        }
        finally
        {
            _busy = false;
            TranslationFetchModelsButton.IsEnabled = true;
        }
    }

    // ---------- OCR 识别 ----------

    private void OnOcrConfigLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (VisionApiModelBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
        {
            _services.Settings.SetOcrVisionModel(model);
        }
        _services.Settings.SetOcrPrompt(VisionApiPromptBox.Text);
    }

    private void OnResetVisionPromptClicked(object sender, RoutedEventArgs e)
    {
        VisionApiPromptBox.Text = SettingsService.DefaultOcrPrompt;
        _services.Settings.SetOcrPrompt(SettingsService.DefaultOcrPrompt);
    }

    private void ApplyOcrEnginePanels(OcrEngineType engine)
    {
        LocalOcrPanel.Visibility = engine == OcrEngineType.Local ? Visibility.Visible : Visibility.Collapsed;
        VisionApiPanel.Visibility = engine == OcrEngineType.VisionApi ? Visibility.Visible : Visibility.Collapsed;
        RefreshOcrEngineHint(engine);
    }

    private void RefreshOcrEngineHint(OcrEngineType? engine = null)
    {
        if (OcrEngineHint == null) return;
        engine ??= _services.Settings.OcrEngine;
        OcrEngineHint.Text = engine switch
        {
            OcrEngineType.Local => "本地离线运行，无需网络。",
            OcrEngineType.VisionApi => "使用 AI 模型服务识别与排版。",
            _ => "系统自带引擎，免配置开箱即用。"
        };
    }

    // ---------- 文本翻译 ----------

    private void OnTranslationEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var engine = TranslationEngineBox.SelectedIndex switch
        {
            1 => TranslationEngineType.Google,
            2 => TranslationEngineType.Ai,
            _ => TranslationEngineType.Bing
        };
        ApplyTranslationEnginePanels(engine);
        if (_services.Settings.TranslationEngine != engine)
        {
            _services.Settings.SetTranslationEngine(engine);
        }
        Dispatcher.BeginInvoke(RefreshScrollExtent, DispatcherPriority.Loaded);
    }

    private void ApplyTranslationEnginePanels(TranslationEngineType engine)
    {
        AiTranslationPanel.Visibility = engine == TranslationEngineType.Ai ? Visibility.Visible : Visibility.Collapsed;
        RefreshTranslationEngineHint(engine);
    }

    private void RefreshTranslationEngineHint(TranslationEngineType? engine = null)
    {
        engine ??= _services.Settings.TranslationEngine;
        if (TranslationEngineHint != null)
        {
            TranslationEngineHint.Text = engine switch
            {
                TranslationEngineType.Google => "需代理环境（由 Google 翻译提供）。",
                TranslationEngineType.Ai => "使用配置的 AI 大模型进行翻译与智能润色。",
                _ => "由 微软翻译 提供。"
            };
        }

        if (TranslationTipIcon != null)
        {
            TranslationTipIcon.ToolTip = engine switch
            {
                TranslationEngineType.Google => "Google 翻译：无需配置 API Key，需代理环境。",
                TranslationEngineType.Ai => "AI 模型翻译：使用配置的 AI 模型服务进行翻译与智能润色。",
                _ => "微软翻译：无需配置 API Key，适合日常单词与短句快速互译。"
            };
        }
    }

    // ---------- 视觉/翻译模型下拉选择与配置保存 ----------

    private void OnVisionApiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (VisionApiModelBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
        {
            _services.Settings.SetOcrVisionModel(model);
        }
    }

    private void OnTranslationModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (TranslationModelBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
        {
            _services.Settings.SetTranslationModel(model);
        }
    }

    private void OnTranslationConfigLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (TranslationModelBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
        {
            _services.Settings.SetTranslationModel(model);
        }
        _services.Settings.SetTranslationPrompt(TranslationPromptBox.Text);
    }

    private void OnResetTranslationPromptClicked(object sender, RoutedEventArgs e)
    {
        TranslationPromptBox.Text = SettingsService.DefaultTranslationPrompt;
        _services.Settings.SetTranslationPrompt(SettingsService.DefaultTranslationPrompt);
    }

    private void OnOcrPackCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (_suppressUiEvents || sender is not FrameworkElement element)
        {
            return;
        }

        if (!TryParseOcrPack(element.Tag, out var pack))
        {
            return;
        }

        SelectOcrPack(pack);
    }

    private async void OnOcrPackActionClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_suppressUiEvents || sender is not FrameworkElement element)
        {
            return;
        }

        if (!TryParseOcrPack(element.Tag, out var pack) || pack == OcrLocalPack.Custom)
        {
            return;
        }

        SelectOcrPack(pack);

        if (_services.OcrPacks.IsDownloading)
        {
            if (_services.OcrPacks.DownloadingPack == pack)
            {
                _services.OcrPacks.CancelDownload();
            }

            return;
        }

        if (_services.OcrPacks.IsOfficialInstalled(pack))
        {
            _services.OcrPacks.DeleteOfficial(pack);
            _services.OcrPacks.InvalidateEngine();
            RefreshLocalOcrPanel();
            return;
        }

        try
        {
            // 先启动任务（同步设置 IsDownloading），再刷 UI，避免按钮仍显示「下载」被点第二次取消
            Task downloadTask = _services.OcrPacks.DownloadAsync(pack);
            RefreshLocalOcrPanel();
            await downloadTask;
            _services.OcrPacks.InvalidateEngine();
        }
        catch (OperationCanceledException)
        {
            DebugLog.Log("设置页取消 OCR 模型下载: " + pack);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("设置页下载 OCR 模型失败", ex);
        }
        finally
        {
            RefreshLocalOcrPanel();
        }
    }

    private void OnOcrCustomDirLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        _services.Settings.SetOcrCustomDir(OcrCustomDirBox.Text);
        _services.OcrPacks.InvalidateEngine();
        RefreshLocalOcrPanel();
    }

    private void OnOcrCustomBrowseClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectOcrPack(OcrLocalPack.Custom);

        var dialog = new OpenFolderDialog
        {
            Title = "选择 OCR 模型目录"
        };

        if (!string.IsNullOrWhiteSpace(OcrCustomDirBox.Text) && Directory.Exists(OcrCustomDirBox.Text))
        {
            dialog.InitialDirectory = OcrCustomDirBox.Text;
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        OcrCustomDirBox.Text = dialog.FolderName;
        _services.Settings.SetOcrCustomDir(dialog.FolderName);
        _services.OcrPacks.InvalidateEngine();
        RefreshLocalOcrPanel();
    }

    private OcrDownloadProgress? _ocrDownloadProgress;

    private void OnOcrDownloadProgress(OcrDownloadProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _ocrDownloadProgress = progress;
            TextBlock status = progress.Pack == OcrLocalPack.Small
                ? OcrPackSmallStatus
                : OcrPackMediumStatus;
            status.Text = progress.Message;
            OcrLocalActiveHint.Text = DescribeLocalActiveModel();
            RefreshLocalOcrButtons();
        }, DispatcherPriority.Render);
    }

    private void OnOcrPacksChanged()
    {
        Dispatcher.BeginInvoke(RefreshLocalOcrPanel);
    }

    private void SelectOcrPack(OcrLocalPack pack)
    {
        if (OcrCustomDirBox.Text != _services.Settings.OcrCustomDir)
        {
            _services.Settings.SetOcrCustomDir(OcrCustomDirBox.Text);
        }

        if (_services.Settings.OcrLocalPack != pack)
        {
            _services.Settings.SetOcrLocalPack(pack);
            _services.OcrPacks.InvalidateEngine();
        }

        RefreshLocalOcrPanel();
    }

    private void RefreshLocalOcrPanel()
    {
        if (LocalOcrPanel == null)
        {
            return;
        }

        ApplyPackCardChrome(OcrPackSmallCard, _services.Settings.OcrLocalPack == OcrLocalPack.Small);
        ApplyPackCardChrome(OcrPackMediumCard, _services.Settings.OcrLocalPack == OcrLocalPack.Medium);
        ApplyPackCardChrome(OcrPackCustomCard, _services.Settings.OcrLocalPack == OcrLocalPack.Custom);

        OcrPackSmallStatus.Text = _services.OcrPacks.OfficialStatusText(OcrLocalPack.Small);
        OcrPackMediumStatus.Text = _services.OcrPacks.OfficialStatusText(OcrLocalPack.Medium);

        OcrPackCustomStatus.Text = CustomPackStatus();
        OcrLocalActiveHint.Text = DescribeLocalActiveModel();
        if (!_services.OcrPacks.IsDownloading)
        {
            _ocrDownloadProgress = _services.OcrPacks.CurrentProgress;
        }

        RefreshLocalOcrButtons();
        if (_services.Settings.OcrEngine == OcrEngineType.Local)
        {
            RefreshOcrEngineHint(OcrEngineType.Local);
        }

        Dispatcher.BeginInvoke(RefreshScrollExtent, DispatcherPriority.Loaded);
    }

    private void RefreshLocalOcrButtons()
    {
        bool downloading = _services.OcrPacks.IsDownloading;
        var current = _services.OcrPacks.DownloadingPack;
        SetOfficialButton(OcrPackSmallButton, OcrLocalPack.Small, downloading, current);
        SetOfficialButton(OcrPackMediumButton, OcrLocalPack.Medium, downloading, current);
        ApplyDownloadBar(OcrPackSmallProgress, OcrLocalPack.Small, downloading, current);
        ApplyDownloadBar(OcrPackMediumProgress, OcrLocalPack.Medium, downloading, current);
    }

    private void ApplyDownloadBar(
        System.Windows.Controls.ProgressBar bar,
        OcrLocalPack pack,
        bool downloading,
        OcrLocalPack? current)
    {
        bool show = downloading && current == pack;
        bar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            bar.IsIndeterminate = false;
            bar.Value = 0;
            return;
        }

        var live = _ocrDownloadProgress ?? _services.OcrPacks.CurrentProgress;
        bar.IsIndeterminate = live?.IsIndeterminate == true;
        bar.Value = live is { Pack: var livePack } && livePack == pack
            ? live.Percent
            : 0;
    }

    private void SetOfficialButton(
        Wpf.Ui.Controls.Button button,
        OcrLocalPack pack,
        bool downloading,
        OcrLocalPack? current)
    {
        if (downloading)
        {
            bool thisPack = current == pack;
            button.Content = thisPack ? "取消" : "下载";
            button.IsEnabled = thisPack;
            return;
        }

        button.IsEnabled = true;
        button.Content = _services.OcrPacks.IsOfficialInstalled(pack) ? "删除" : "下载";
    }

    private static void ApplyPackCardChrome(Border card, bool selected)
    {
        card.SetResourceReference(
            Border.BackgroundProperty,
            selected ? ThemeService.CardSelectedBrushKey : ThemeService.SearchBrushKey);
        card.SetResourceReference(
            Border.BorderBrushProperty,
            selected ? ThemeService.AccentBrushKey : ThemeService.BorderBrushKey);
        card.BorderThickness = new Thickness(selected ? 1.5 : 1);
    }

    /// <summary>模型包下方的当前启动提示：已选档位 + 是否真正能跑。</summary>
    private string DescribeLocalActiveModel()
    {
        var resolved = _services.OcrPacks.ResolveCurrent();
        string name = string.IsNullOrWhiteSpace(resolved.Title) ? "离线模型" : resolved.Title;

        if (_services.OcrPacks.IsDownloading &&
            _services.OcrPacks.DownloadingPack == _services.Settings.OcrLocalPack)
        {
            return "当前启动：" + name + "（正在下载…）";
        }

        if (resolved.Error != null)
        {
            string reason = resolved.Error.StartsWith("未下载", StringComparison.Ordinal)
                ? "未下载"
                : resolved.Error;
            return "当前启动：" + name + "（" + reason + "，识别时回退系统 OCR）";
        }

        return "当前启动：" + name + "（已就绪）";
    }

    private string CustomPackStatus()
    {
        string dir = OcrCustomDirBox.Text;
        var resolved = _services.OcrPacks.InspectCustom(dir);
        if (resolved.Error != null)
        {
            return resolved.Error;
        }

        long bytes = 0;
        foreach (string? path in new[] { resolved.DetPath, resolved.RecPath, resolved.KeysPath })
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
            }
        }

        string size = bytes > 0 ? " · " + (bytes / (1024d * 1024d)).ToString("0.0") + " MB" : string.Empty;
        return "已就绪" + size;
    }

    private static bool TryParseOcrPack(object? tag, out OcrLocalPack pack) =>
        Enum.TryParse(tag?.ToString(), ignoreCase: true, out pack);

    // ---------- 文案 ----------

    private static string DescribeTag(object? tag) => tag switch
    {
        "PasteSelected" => "粘贴选中项",
        "PasteSelectedPlain" => "纯文本粘贴选中项",
        "CopySelected" => "复制选中项",
        "TogglePin" => "窗口置顶",
        "DeleteSelected" => "删除选中项",
        "HidePanel" => "隐藏面板",
        "MoveUp" => "选中上一项",
        "MoveDown" => "选中下一项",
        "StartStack" => "开启/关闭收集栈",
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
        PanelHotkeyAction.StartStack => "开启/关闭收集栈",
        _ => action.ToString()
    };
}
