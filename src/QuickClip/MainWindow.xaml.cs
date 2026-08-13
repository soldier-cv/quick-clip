using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickClip.Services;
using QuickClip.ViewModels;
using Wpf.Ui.Controls;

namespace QuickClip;

/// <summary>QuickClip 浮动主窗口。</summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;

    /// <summary>视图模型（设置窗口切换数据库后需要刷新列表）。</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _services = services;

        // 与设置页同一不透明底 + 关闭 DWM 系统材质
        bool remoteRender = RenderEnvironment.IsRemoteOrVirtualDisplay();
        DebugLog.Log(
            $"渲染环境检测: remoteRender={remoteRender}" +
            (RenderEnvironment.LastMatchReason is { } r ? $", reason={r}" : ""));
        WindowChromeHelper.Apply(this, RootGrid);
        // 预览圆角与软件渲染解耦：Win10 本机可透明圆角；仅真 RDP 才直角不透明
        ApplyHoverPreviewTheme();
        if (remoteRender)
        {
            DebugLog.Log("已启用渲染降级：软件渲染（预览仍可圆角，除非 RDP 会话）");
        }

        DataContext = _viewModel;

        // 事件接线
        _viewModel.QrImageReady += ShowQrOverlay;
        _viewModel.OcrResultReady += ShowOcrOverlay;
        _services.Hotkey.ToggleRequested += ToggleWindow;
        _services.Hotkey.PastePlainRequested += OnPastePlainRequested;
        _services.Tray.ToggleRequested += ToggleWindow;
        _services.Tray.ExitRequested += ExitApp;
        _services.Tray.SettingsRequested += OpenSettingsWindow;
        _services.Tray.AutoStartToggleRequested += ToggleAutoStart;
        _services.Tray.CheckUpdateRequested += CheckUpdateAsync;
        _services.Tray.InstallUpdateRequested += ApplyPendingUpdate;
        _services.Tray.OpenDataFolderRequested += OpenDataFolderFromTray;
        _services.Tray.ClearTodayHistoryRequested += ClearTodayFromTray;

        PositionWindow();

        // 预览浮层：离开条目/浮层 280ms 后自动关闭
        _previewCloseTimer.Tick += (_, _) =>
        {
            _previewCloseTimer.Stop();
            PreviewPopup.IsOpen = false;
        };

        // 窗口置顶联动：设置变更（Ctrl+P / 标题栏按钮 / 设置窗口）即时生效
        _services.Settings.Changed += OnSettingsChanged;
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            _services.Settings.Changed -= OnSettingsChanged;
            ThemeService.Changed -= OnThemeChanged;
        };
        OnSettingsChanged();
    }

    private void OnThemeChanged()
    {
        WindowChromeHelper.Apply(this, RootGrid);
        ApplyHoverPreviewTheme();
        OnSettingsChanged();
    }

    /// <summary>
    /// 悬停预览 Popup 样式（Win10/11 均支持 WPF 圆角，与系统窗口圆角无关）：
    /// · 本机：透明宿主 + 圆角卡片 + 阴影
    /// · 真 RDP：不透明矩形 HWND，取消圆角铺满同色（避免黑方块托灰圆角）
    /// </summary>
    private void ApplyHoverPreviewTheme()
    {
        // 仅真远程桌面会话强制直角；软件渲染本机仍用圆角透明
        bool opaquePopup = RenderEnvironment.RequiresOpaquePopup();
        var p = ThemeService.CurrentPalette;
        var card = WindowChromeHelper.CreateSolidBrush(p.Card);
        var border = WindowChromeHelper.CreateSolidBrush(p.Border);

        if (opaquePopup)
        {
            PreviewPopup.AllowsTransparency = false;
            PreviewPopup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None;
            PreviewBorder.Effect = null;
            PreviewBorder.CornerRadius = new CornerRadius(0);
            PreviewRoot.Background = card;
            PreviewBorder.Background = card;
            PreviewBorder.BorderBrush = border;
            PreviewBorder.BorderThickness = new Thickness(1);
            PreviewBorder.Margin = new Thickness(0);
        }
        else
        {
            PreviewPopup.AllowsTransparency = true;
            PreviewPopup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade;
            PreviewRoot.Background = System.Windows.Media.Brushes.Transparent;
            PreviewBorder.Background = card;
            PreviewBorder.BorderBrush = border;
            PreviewBorder.BorderThickness = new Thickness(1);
            PreviewBorder.CornerRadius = new CornerRadius(12);
            PreviewBorder.Margin = new Thickness(8);
            if (PreviewShadow != null)
            {
                PreviewBorder.Effect = PreviewShadow;
                PreviewShadow.Opacity = p.IsDark ? 0.5 : 0.28;
                PreviewShadow.BlurRadius = 20;
            }
        }

        var accent = WindowChromeHelper.CreateSolidBrush(p.Accent);
        var text = WindowChromeHelper.CreateSolidBrush(p.Text);
        var secondary = WindowChromeHelper.CreateSolidBrush(p.TextSecondary);
        var muted = WindowChromeHelper.CreateSolidBrush(p.TextMuted);

        PreviewTypeIcon.Foreground = accent;
        PreviewMetaType.Foreground = secondary;
        PreviewMetaTime.Foreground = secondary;
        PreviewMetaSize.Foreground = secondary;
        PreviewMetaDot1.Foreground = muted;
        PreviewMetaDot2.Foreground = muted;
        PreviewBodyText.Foreground = text;
        PreviewQrCaption.Foreground = muted;
        PreviewScroll.Background = System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>窗口句柄创建后挂载剪贴板监听。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _services.Monitor.Attach(this);
    }

    /// <summary>关闭按钮 / Alt+F4 视为隐藏，避免误退出。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // 失焦即隐：点到其他应用时隐藏（仿系统 Win+V）。
        // 打开设置窗时主窗也会失焦，不能当「点到外部」——否则列表会被误收起。
        if (IsVisible && !_exiting && !_services.Settings.WindowAlwaysOnTop &&
            QrOverlay.Visibility == Visibility.Collapsed &&
            OcrOverlay.Visibility == Visibility.Collapsed &&
            !IsSettingsWindowOpen())
        {
            PreviewPopup.IsOpen = false;
            Hide();
        }
    }

    /// <summary>设置窗口是否正在显示（含刚激活、主窗暂失焦的情况）。</summary>
    private bool IsSettingsWindowOpen() =>
        _settingsWindow is { IsVisible: true };

    private void OnTitleBarCloseClicked(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    /// <summary>唤起 / 切换窗口（可由键盘钩子或托盘触发）。</summary>
    private void ToggleWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ToggleWindow);
            return;
        }

        DebugLog.Log($"ToggleWindow 触发, IsVisible={IsVisible}");
        if (IsVisible)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        // 记录唤起前的目标窗口，用于粘贴回填
        _services.Paste.RememberTargetWindow();
        PositionWindow();
        Show();
        Activate();

        // 恢复上次选中；否则默认第 1 条（最近一条），Enter 即贴
        if (_viewModel.RememberedSelectedId is long id)
        {
            var match = _viewModel.Items.FirstOrDefault(x => x.Item.Id == id);
            if (match != null)
            {
                _viewModel.SelectedItem = match;
                ItemList.ScrollIntoView(match);
            }
            else if (_viewModel.Items.Count > 0)
            {
                _viewModel.SelectedItem = _viewModel.Items[0];
            }
        }
        else if (_viewModel.SelectedItem == null && _viewModel.Items.Count > 0)
        {
            _viewModel.SelectedItem = _viewModel.Items[0];
        }

        SearchBox.Focus();
        SearchBox.SelectAll();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // 先按“窗口置顶”设置同步 Win32 z-order，修复隐藏/显示后置顶被 WPF Topmost 短路丢失的问题
            ApplyTopmostState(handle);

            // 兜底置前：热键唤起时系统前台锁可能阻止 Activate（面板“呼不出”或落到其他窗口后面）。
            // 仅在前台仍是其他窗口时才临时置顶强制带到最前；避免每次唤起都做 TOPMOST→NOTOPMOST
            // 往返（在远程/虚拟显示环境下会引发整屏闪烁）。
            if (QuickClip.Native.NativeMethods.GetForegroundWindow() != handle)
            {
                const uint flags = QuickClip.Native.NativeMethods.SWP_NOMOVE |
                                   QuickClip.Native.NativeMethods.SWP_NOSIZE |
                                   QuickClip.Native.NativeMethods.SWP_SHOWWINDOW;
                QuickClip.Native.NativeMethods.SetWindowPos(handle, QuickClip.Native.NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
                QuickClip.Native.NativeMethods.SetForegroundWindow(handle);
                ApplyTopmostState(handle);
            }
        }

        DebugLog.Log("窗口已显示");
    }

    /// <summary>按“窗口置顶”设置强制同步 Win32 z-order，避免 WPF Topmost 属性值未变化时不再重新下发。</summary>
    private void ApplyTopmostState(IntPtr handle)
    {
        bool pinned = _services.Settings.WindowAlwaysOnTop;
        QuickClip.Native.NativeMethods.SetWindowPos(
            handle,
            pinned ? QuickClip.Native.NativeMethods.HWND_TOPMOST : QuickClip.Native.NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            QuickClip.Native.NativeMethods.SWP_NOMOVE |
            QuickClip.Native.NativeMethods.SWP_NOSIZE |
            QuickClip.Native.NativeMethods.SWP_NOACTIVATE);
    }

    private void HideWindow()
    {
        PreviewPopup.IsOpen = false;
        // 记住选中，便于再次 Win+V 恢复位置
        _viewModel.RememberedSelectedId = _viewModel.SelectedItem?.Item.Id;
        Hide();
        DebugLog.Log("窗口已隐藏");
    }

    /// <summary>
    /// 全局 Ctrl+Shift+V：面板在前台时粘贴选中条目纯文本，
    /// 否则将当前系统剪贴板以纯文本粘贴到前台窗口。
    /// </summary>
    private void OnPastePlainRequested()
    {
        if (IsVisible && IsActive)
        {
            PasteSelected(true);
        }
        else
        {
            _services.Paste.RememberTargetWindow();
            _services.Paste.PastePlainTextFromClipboard();
        }
    }

    /// <summary>真正退出进程（托盘退出）。</summary>
    public void RequestExit() => ExitApp();

    private void ExitApp()
    {
        _exiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 主面板停靠到当前显示器工作区右侧并垂直居中（仿 Win+V）。
    /// 小分辨率时收缩宽高，避免超出屏幕。
    /// </summary>
    private void PositionWindow()
    {
        var workArea = GetWindowWorkArea();
        const double margin = 12;

        // 小屏：限制高度 / 宽度（DIP，已按 DPI 换算）
        double maxHeight = Math.Max(360, workArea.Height - margin * 2);
        double maxWidth = Math.Max(320, workArea.Width - margin * 2);
        if (Height > maxHeight)
        {
            Height = maxHeight;
        }

        if (Width > maxWidth)
        {
            Width = maxWidth;
        }

        Left = workArea.Right - Width - margin;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        // 钳制在工作区内（多显示器 / 极端 DPI）
        Left = Math.Clamp(Left, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - Width - margin));
        Top = Math.Clamp(Top, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - Height - margin));
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        key = Models.HotkeyBinding.NormalizeKey(key);
        var modifiers = Keyboard.Modifiers &
                        (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        var settings = _services.Settings;

        // 面板快捷键一律读设置（与帮助气泡、设置页同一套绑定）
        if (QrOverlay.Visibility == Visibility.Visible ||
            OcrOverlay.Visibility == Visibility.Visible)
        {
            if (settings.HidePanelHotkey.Matches(key, modifiers))
            {
                OnCloseOverlays(sender, e);
                e.Handled = true;
            }

            return;
        }

        if (settings.HidePanelHotkey.Matches(key, modifiers))
        {
            HideWindow();
            e.Handled = true;
            return;
        }

        if (settings.PasteSelectedPlainHotkey.Matches(key, modifiers))
        {
            PasteSelected(plainOnly: true);
            e.Handled = true;
            return;
        }

        if (settings.PasteSelectedHotkey.Matches(key, modifiers))
        {
            PasteSelected(plainOnly: false);
            e.Handled = true;
            return;
        }

        if (settings.DeleteSelectedHotkey.Matches(key, modifiers))
        {
            _ = _viewModel.DeleteSelectedAsync();
            e.Handled = true;
            return;
        }

        if (settings.TogglePinHotkey.Matches(key, modifiers))
        {
            ToggleWindowPin();
            e.Handled = true;
            return;
        }

        if (settings.CopySelectedHotkey.Matches(key, modifiers) && !IsSearchFocused())
        {
            _ = _viewModel.CopySelectedToClipboard();
            e.Handled = true;
            return;
        }

        if (settings.MoveDownHotkey.Matches(key, modifiers))
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (settings.MoveUpHotkey.Matches(key, modifiers))
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        // 1~9 / 小键盘：固定快速粘贴
        if (modifiers == ModifierKeys.None && !IsSearchFocused())
        {
            if (key is >= Key.D1 and <= Key.D9)
            {
                PasteItemAt((int)key - (int)Key.D0);
                e.Handled = true;
            }
            else if (key is >= Key.NumPad1 and <= Key.NumPad9)
            {
                PasteItemAt((int)key - (int)Key.NumPad0);
                e.Handled = true;
            }
        }
    }

    private static bool IsSearchFocused() => Keyboard.FocusedElement is System.Windows.Controls.TextBox;

    private void PasteItemAt(int index)
    {
        var vm = _viewModel.GetItemAt(index - 1);
        if (vm == null)
        {
            return;
        }

        _viewModel.SelectedItem = vm;
        PasteSelected(false);
    }

    private void PasteSelected(bool plainOnly)
    {
        // 置顶（Ctrl+P / 图钉）= 工作会话：失焦不藏、粘贴后也不关；未置顶则粘贴后隐藏
        bool pinned = _services.Settings.WindowAlwaysOnTop;
        if (!pinned)
        {
            HideWindow();
        }

        _viewModel.PasteSelected(plainOnly);
        if (pinned)
        {
            _viewModel.StatusText = "已粘贴（置顶中，面板保持打开）";
        }
    }

    private void MoveSelection(int delta)
    {
        if (_viewModel.Items.Count == 0)
        {
            return;
        }

        int current = _viewModel.SelectedItem == null
            ? 0
            : _viewModel.Items.IndexOf(_viewModel.SelectedItem);
        int target = Math.Clamp(current + delta, 0, _viewModel.Items.Count - 1);
        _viewModel.SelectedItem = _viewModel.Items[target];
        ItemList.ScrollIntoView(_viewModel.SelectedItem);
    }

    // ---------- 卡片鼠标操作 ----------

    // 关闭延迟略加长：浮层与主窗口之间有间距，鼠标穿越时不应立刻消失
    private readonly DispatcherTimer _previewCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FrameworkElement? _hoverCard;
    /// <summary>预览是否处于「二维码按钮悬停」模式。</summary>
    private bool _previewQrMode;
    private int _previewQrGeneration;
    private readonly Dictionary<string, BitmapImage> _qrPreviewCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 悬浮预览：悬停条目时在窗口左侧/右侧弹出深色预览浮层（单个共享实例），
    /// 垂直对齐到条目中心；浮层可被悬停而不消失，离开后延迟关闭。
    /// StaysOpen=True，仅由定时器 / 窗口隐藏关闭，避免 hover 打开后被 Popup 自关闭。
    /// 已打开时直接换 DataContext 并重定位，避免依赖 Opened 事件导致卡在错误位置。
    /// </summary>
    private void OnCardMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardItemViewModel vm } card)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _hoverCard = card;
        // 从二维码按钮回到卡片正文时恢复普通预览
        if (_previewQrMode)
        {
            _previewQrMode = false;
            _previewQrGeneration++;
        }

        ShowItemHoverPreview(vm, card);
    }

    /// <summary>打开/切换条目的普通悬停预览（文本或图片）。</summary>
    private void ShowItemHoverPreview(ClipboardItemViewModel vm, FrameworkElement? card = null)
    {
        if (card != null)
        {
            _hoverCard = card;
        }

        ApplyHoverPreviewTheme();
        PreviewPopup.DataContext = vm;
        SetPreviewContentMode(showQr: false);
        PreviewPopup.PlacementTarget = this;
        PreviewPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;

        bool alreadyOpen = PreviewPopup.IsOpen;
        if (!alreadyOpen)
        {
            PreviewPopup.HorizontalOffset = 0;
            PreviewPopup.VerticalOffset = 0;
            PreviewPopup.Opened -= OnPreviewOpened;
            PreviewPopup.Opened += OnPreviewOpened;
            PreviewPopup.IsOpen = true;
            DebugLog.Log($"预览浮层打开: type={vm.TypeLabel}");
        }
        else
        {
            RepositionPreview();
            Dispatcher.BeginInvoke(RepositionPreview, DispatcherPriority.Loaded);
            DebugLog.Log($"预览浮层切换: type={vm.TypeLabel}");
        }
    }

    /// <summary>切换预览内容：普通（图/文）或二维码。退出二维码模式时清掉本地 Visibility，避免盖住绑定。</summary>
    private void SetPreviewContentMode(bool showQr)
    {
        if (showQr)
        {
            PreviewHoverImage.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Collapsed;
            PreviewQrPanel.Visibility = Visibility.Visible;
            return;
        }

        PreviewQrPanel.Visibility = Visibility.Collapsed;
        PreviewQrImage.Source = null;
        PreviewHoverImage.ClearValue(UIElement.VisibilityProperty);
        PreviewScroll.ClearValue(UIElement.VisibilityProperty);
    }

    /// <summary>内容测量完成后按条目位置重定位浮层（左右自适应 + 垂直对齐 + 屏幕边界钳制）。</summary>
    private void OnPreviewOpened(object? sender, EventArgs e)
    {
        PreviewPopup.Opened -= OnPreviewOpened;
        RepositionPreview();

        // 初次定位时内容可能尚未完成测量，布局完成后二次校正（长文本/图片场景）
        Dispatcher.BeginInvoke(RepositionPreview, DispatcherPriority.Loaded);
    }

    private void RepositionPreview()
    {
        if (_hoverCard is not { } card || PreviewRoot is not { } root)
        {
            return;
        }

        // 强制测量未布局完成的内容，避免首次打开时 ActualWidth/Height 为 0
        if (root.ActualWidth <= 0 || root.ActualHeight <= 0)
        {
            root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new System.Windows.Rect(root.DesiredSize));
        }

        double popupWidth = root.ActualWidth > 1 ? root.ActualWidth : Math.Max(root.DesiredSize.Width, 160);
        double popupHeight = root.ActualHeight > 1 ? root.ActualHeight : Math.Max(root.DesiredSize.Height, 80);
        if (popupWidth < 1 || popupHeight < 1)
        {
            popupWidth = 280;
            popupHeight = 120;
        }

        System.Windows.Point cardCenter = card.TranslatePoint(
            new System.Windows.Point(card.ActualWidth / 2, card.ActualHeight / 2), this);

        // 优先用窗口所在显示器的工作区（多屏时 SystemParameters.WorkArea 仅是主屏）
        var workArea = GetWindowWorkArea();

        // 优先放在窗口外侧有足够空间的一侧；两侧都够时：窗口偏右放左，偏左放右
        double gap = 8;
        double spaceLeft = Left - workArea.Left;
        double spaceRight = workArea.Right - (Left + ActualWidth);
        bool toLeft = spaceLeft >= popupWidth + gap && (spaceLeft >= spaceRight || spaceRight < popupWidth + gap);
        // 两侧都不够时仍选空间更大的一侧，随后由水平钳制拉回可视区
        if (spaceLeft < popupWidth + gap && spaceRight < popupWidth + gap)
        {
            toLeft = spaceLeft >= spaceRight;
        }

        double offsetX = toLeft ? -(popupWidth + gap) : ActualWidth + gap;

        // 垂直对齐条目中心（相对窗口顶部），并钳制在工作区内
        double offsetY = cardCenter.Y - popupHeight / 2;
        double screenTop = Top + offsetY;
        if (screenTop < workArea.Top + 8)
        {
            offsetY = workArea.Top + 8 - Top;
        }
        else if (screenTop + popupHeight > workArea.Bottom - 8)
        {
            offsetY = workArea.Bottom - 8 - Top - popupHeight;
        }

        // 水平钳制：浮层不得超出工作区左右边界
        double screenLeft = Left + offsetX;
        if (screenLeft < workArea.Left + 8)
        {
            offsetX = workArea.Left + 8 - Left;
        }
        else if (screenLeft + popupWidth > workArea.Right - 8)
        {
            offsetX = workArea.Right - 8 - Left - popupWidth;
        }

        PreviewPopup.HorizontalOffset = offsetX;
        PreviewPopup.VerticalOffset = offsetY;
        DebugLog.Log($"预览浮层定位: size={popupWidth:F0}x{popupHeight:F0} side={(toLeft ? "左" : "右")} offset=({offsetX:F0},{offsetY:F0})");
    }

    /// <summary>获取主窗口所在（或光标所在）显示器的工作区，单位 DIP。</summary>
    private System.Windows.Rect GetWindowWorkArea()
    {
        try
        {
            System.Windows.Forms.Screen screen;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            }
            else
            {
                // 句柄未就绪：按鼠标所在屏（多显示器更合理）
                screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            }

            var bounds = screen.WorkingArea;
            GetDpiScale(out double dpiX, out double dpiY);
            return new System.Windows.Rect(
                bounds.Left / dpiX,
                bounds.Top / dpiY,
                bounds.Width / dpiX,
                bounds.Height / dpiY);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private void GetDpiScale(out double dpiX, out double dpiY)
    {
        var source = System.Windows.PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
            if (dpiX > 0 && dpiY > 0)
            {
                return;
            }
        }

        dpiX = 1.0;
        dpiY = 1.0;
    }

    /// <summary>
    /// 设置窗优先放在主列表左侧并垂直对齐，避免与右侧列表重叠；
    /// 左侧空间不足则试右侧，再不行则工作区内居中并钳制。
    /// </summary>
    private void PositionSettingsBesideMain(SettingsWindow settings)
    {
        var workArea = GetWindowWorkArea();
        const double gap = 12;
        const double margin = 8;

        // 小屏：设置窗不超过工作区（只改 Height/Width，禁止 Measure(∞)）
        // Measure(∞) 会让内部 ScrollViewer 按「无限高视口」量内容 → ScrollableHeight=0，
        // 表现为：刚打开滚不到底，拖一下窗口触发布局后又能滚。
        double maxH = Math.Max(settings.MinHeight, workArea.Height - margin * 2);
        double maxW = Math.Max(settings.MinWidth, workArea.Width - margin * 2);
        if (settings.Height > maxH)
        {
            settings.Height = maxH;
        }

        if (settings.Width > maxW)
        {
            settings.Width = maxW;
        }

        // 用显式尺寸定位；未完成布局时用 Width/Height
        double w = settings.ActualWidth > 1 ? settings.ActualWidth : settings.Width;
        double h = settings.ActualHeight > 1 ? settings.ActualHeight : settings.Height;

        double mainLeft = Left;
        double mainRight = Left + ActualWidth;
        double mainCenterY = Top + ActualHeight / 2;

        double leftCandidate = mainLeft - w - gap;
        double rightCandidate = mainRight + gap;
        double topCandidate = mainCenterY - h / 2;

        double x;
        if (leftCandidate >= workArea.Left + margin)
        {
            // 优先：列表左侧
            x = leftCandidate;
        }
        else if (rightCandidate + w <= workArea.Right - margin)
        {
            // 次选：列表右侧（少见，主窗不在最右时）
            x = rightCandidate;
        }
        else
        {
            // 空间都不够：工作区水平居中
            x = workArea.Left + (workArea.Width - w) / 2;
        }

        double y = topCandidate;
        x = Math.Clamp(x, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - w - margin));
        y = Math.Clamp(y, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - h - margin));

        settings.Left = x;
        settings.Top = y;

        // 定位后按有限视口重测滚动范围（修复首次打不开底部）
        settings.RefreshScrollExtent();
        DebugLog.Log($"设置窗定位: ({x:F0},{y:F0}) size={w:F0}x{h:F0} work={workArea.Width:F0}x{workArea.Height:F0}");
    }

    private void OnCardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 进入同一卡片内子元素时也会冒泡 MouseLeave，忽略仍在卡片内的情况
        if (sender is FrameworkElement card && card.IsMouseOver)
        {
            return;
        }

        _previewCloseTimer.Start();
    }

    private void OnPreviewMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
    }

    private void OnPreviewMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 进入浮层内部子元素时忽略；真正离开内容根再启动关闭倒计时
        if (PreviewRoot.IsMouseOver)
        {
            return;
        }

        _previewCloseTimer.Start();
    }

    /// <summary>单击：仅选中条目（不写系统剪贴板）。复制用卡片「复制」按钮或 Ctrl+C。</summary>
    private void OnItemListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        if (GetCardViewModel(source) is { } vm)
        {
            _viewModel.SelectedItem = vm;
        }
    }

    /// <summary>双击：粘贴选中项（等效 Enter）；不经过单击复制。</summary>
    private void OnItemListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        if (GetCardViewModel(source) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            PasteSelected(false);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static FrameworkElement? FindItemCard(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is FrameworkElement { Name: "ItemCard" } card)
            {
                return card;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // ---------- 卡片动作 ----------

    /// <summary>悬停二维码按钮：文本预览生成码；已识别二维码图保持原图预览。</summary>
    private async void OnQrButtonMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (GetCardViewModel(sender) is not { } vm)
        {
            return;
        }

        if (vm.IsImage && vm.HasQr)
        {
            _previewCloseTimer.Stop();
            _viewModel.SelectedItem = vm;
            if (FindItemCard(sender as DependencyObject) is { } qrCard)
            {
                ShowItemHoverPreview(vm, qrCard);
            }

            return;
        }

        if (!vm.IsText)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _viewModel.SelectedItem = vm;
        _previewQrMode = true;
        int gen = ++_previewQrGeneration;

        if (FindItemCard(sender as DependencyObject) is { } card)
        {
            _hoverCard = card;
        }

        ApplyHoverPreviewTheme();
        PreviewPopup.DataContext = vm;
        PreviewPopup.PlacementTarget = this;
        PreviewPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        SetPreviewContentMode(showQr: true);
        PreviewQrImage.Source = null;
        PreviewQrCaption.Text = "生成中…";

        if (!PreviewPopup.IsOpen)
        {
            PreviewPopup.HorizontalOffset = 0;
            PreviewPopup.VerticalOffset = 0;
            PreviewPopup.Opened -= OnPreviewOpened;
            PreviewPopup.Opened += OnPreviewOpened;
            PreviewPopup.IsOpen = true;
        }

        string? content = ResolveQrContent(vm);
        if (string.IsNullOrWhiteSpace(content))
        {
            if (gen == _previewQrGeneration && _previewQrMode)
            {
                PreviewQrCaption.Text = "当前条目无法生成二维码";
            }

            return;
        }

        try
        {
            BitmapImage? bmp = null;
            if (_qrPreviewCache.TryGetValue(content, out var cached))
            {
                bmp = cached;
            }
            else
            {
                byte[] bytes = await Task.Run(() => _services.Qr.GeneratePng(content, 8));
                if (gen != _previewQrGeneration || !_previewQrMode)
                {
                    return;
                }

                bmp = BytesToBitmap(bytes);
                if (_qrPreviewCache.Count > 32)
                {
                    _qrPreviewCache.Clear();
                }

                _qrPreviewCache[content] = bmp;
            }

            if (gen != _previewQrGeneration || !_previewQrMode)
            {
                return;
            }

            PreviewQrImage.Source = bmp;
            string caption = content.Length <= 48 ? content : content[..48] + "…";
            PreviewQrCaption.Text = caption;
            PreviewTypeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.QrCode24;
            RepositionPreview();
            _ = Dispatcher.BeginInvoke(RepositionPreview, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("悬停生成二维码失败", ex);
            if (gen == _previewQrGeneration && _previewQrMode)
            {
                PreviewQrCaption.Text = "二维码生成失败";
            }
        }
    }

    /// <summary>离开二维码按钮：若仍在卡片上则恢复普通预览，否则延迟关闭。</summary>
    private void OnQrButtonMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewQrMode = false;
        _previewQrGeneration++;

        if (_hoverCard is { IsMouseOver: true } &&
            PreviewPopup.DataContext is ClipboardItemViewModel vm)
        {
            ApplyHoverPreviewTheme();
            SetPreviewContentMode(showQr: false);
            // 恢复类型图标（悬停二维码时可能改成了 QrCode）
            PreviewTypeIcon.Symbol = vm.TypeIcon;
            RepositionPreview();
            return;
        }

        _previewCloseTimer.Start();
    }

    private static string? ResolveQrContent(ClipboardItemViewModel vm) =>
        vm.Item.ContentType switch
        {
            Models.ClipboardContentType.Image when vm.HasQr => vm.QrText,
            Models.ClipboardContentType.Image => null,
            _ => vm.Item.TextContent
        };

    private static BitmapImage BytesToBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void OnQrClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is not { } vm)
        {
            return;
        }

        _viewModel.SelectedItem = vm;
        if (vm.IsImage && vm.HasQr)
        {
            _ = _viewModel.CopyQrTextAsync();
            return;
        }

        _ = _viewModel.GenerateQrForSelectedAsync();
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.CopySelectedToClipboard();
        }
    }

    private void OnOcrClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.OcrSelectedAsync();
        }
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.TogglePinSelectedAsync();
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.DeleteSelectedAsync();
        }
    }

    private static ClipboardItemViewModel? GetCardViewModel(object sender)
    {
        return sender is FrameworkElement { DataContext: ClipboardItemViewModel vm } ? vm : null;
    }

    // ---------- 覆盖层 ----------

    private void ShowQrOverlay(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        QrImage.Source = bitmap;
        QrTextBlock.Text = _viewModel.SelectedItem?.Item.TextContent ?? string.Empty;
        QrOverlay.Visibility = Visibility.Visible;
        QrOverlay.IsHitTestVisible = true;
    }

    private void ShowOcrOverlay(string title, string text)
    {
        OcrTitle.Text = string.IsNullOrWhiteSpace(title) ? "离线识别" : title;
        OcrText.Text = text;
        OcrOverlay.Visibility = Visibility.Visible;
        OcrOverlay.IsHitTestVisible = true;
    }

    private async void OnCopyOcrClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OcrText.Text))
        {
            return;
        }

        try
        {
            await _services.Paste.CopyTextAsync(OcrText.Text);
            _viewModel.StatusText = "OCR 文字已复制";
        }
        catch (Exception ex)
        {
            DebugLog.LogException("复制 OCR 文字失败", ex);
            _viewModel.StatusText = "复制失败，剪贴板可能被占用";
        }
    }

    private void OnCloseOverlays(object sender, RoutedEventArgs e)
    {
        QrOverlay.Visibility = Visibility.Collapsed;
        OcrOverlay.Visibility = Visibility.Collapsed;
    }

    // ---------- 设置与托盘动作 ----------

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private void OnPinWindowClicked(object sender, RoutedEventArgs e)
    {
        ToggleWindowPin();
    }

    /// <summary>切换主窗口前端置顶（固定在最前，失焦不自动隐藏）。</summary>
    private void ToggleWindowPin()
    {
        bool pinned = !_services.Settings.WindowAlwaysOnTop;
        _services.Settings.SetWindowAlwaysOnTop(pinned);
        _viewModel.StatusText = pinned ? "窗口已置顶" : "已取消窗口置顶";
        DebugLog.Log(pinned
            ? $"窗口置顶已开启（{_services.Settings.TogglePinHotkey} / 图钉）"
            : $"窗口置顶已关闭（{_services.Settings.TogglePinHotkey} / 图钉）");
    }

    /// <summary>设置变更联动：应用窗口置顶状态（Topmost + 标题栏按钮视觉 + 帮助提示）。</summary>
    private void OnSettingsChanged()
    {
        bool pinned = _services.Settings.WindowAlwaysOnTop;
        Topmost = pinned;
        // 窗口已创建时同步 Win32 z-order，防止 WPF Topmost 属性短路导致置顶不生效
        if (System.Windows.Interop.HwndSource.FromVisual(this) is System.Windows.Interop.HwndSource hwndSource &&
            hwndSource.Handle != IntPtr.Zero)
        {
            ApplyTopmostState(hwndSource.Handle);
        }

        string pinKeys = _services.Settings.TogglePinHotkey.ToString();
        var palette = ThemeService.CurrentPalette;
        PinWindowIcon.Foreground = new System.Windows.Media.SolidColorBrush(
            pinned ? palette.Accent : palette.TextSecondary);
        PinWindowButton.ToolTip = pinned
            ? $"取消窗口置顶（{pinKeys}）"
            : $"窗口置顶：失焦不隐藏，粘贴后保持打开（{pinKeys}）";

        RefreshHelpTip();
    }

    /// <summary>帮助气泡与设置页共用同一套绑定；Win+V / 1~9 为系统保留。</summary>
    private void RefreshHelpTip()
    {
        if (HelpTipButton is null)
        {
            return;
        }

        var s = _services.Settings;
        string globalPaste = s.PlainPasteEnabled
            ? $"[{s.PlainPasteHotkey}] 全局纯文本粘贴"
            : $"[{s.PlainPasteHotkey}] 全局纯文本粘贴（未启用）";

        string body =
            $"单击选中 · 双击粘贴 · 卡片「复制」或 [{s.CopySelectedHotkey}] 仅复制\n" +
            $"[{s.PasteSelectedHotkey}] 粘贴选中项\n" +
            $"[{s.PasteSelectedPlainHotkey}] 纯文本粘贴选中项\n" +
            $"[1 ~ 9] 快速粘贴第 1~9 条\n" +
            $"{globalPaste}\n" +
            $"[{s.TogglePinHotkey}] 窗口置顶（失焦不藏 / 粘贴不关）\n" +
            $"[{s.HidePanelHotkey}] 隐藏  ·  [{s.DeleteSelectedHotkey}] 删除\n" +
            $"[{Models.HotkeyBinding.WinV}] 唤起 QuickClip";

        var palette = ThemeService.CurrentPalette;
        var tip = new System.Windows.Controls.ToolTip
        {
            Background = WindowChromeHelper.CreateSolidBrush(palette.Card),
            Content = new System.Windows.Controls.TextBlock
            {
                Text = body,
                Foreground = WindowChromeHelper.CreateSolidBrush(palette.Text),
                FontSize = 12,
                LineHeight = 20
            }
        };
        HelpTipButton.ToolTip = tip;
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            PositionSettingsBesideMain(_settingsWindow);
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_services)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;

        // 始终贴主列表左侧定位，不记忆拖动位置（避免乱飞）
        _settingsWindow.Show();
        PositionSettingsBesideMain(_settingsWindow);
        _settingsWindow.Activate();
    }

    private void OpenDataFolderFromTray()
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
            DebugLog.LogException("托盘打开数据目录失败", ex);
        }
    }

    private async void ClearTodayFromTray()
    {
        await _viewModel.ClearTodayAndRefreshAsync();
        _services.Tray.ShowBalloonTip("QuickClip", _viewModel.StatusText);
    }

    /// <summary>列表工具栏：清空全部非置顶历史（置顶保留）。</summary>
    private async void OnClearListClicked(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "将删除全部非置顶历史记录，置顶条目会保留。\n确定清空列表？",
            "清空列表",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        PreviewPopup.IsOpen = false;
        await _viewModel.ClearAllUnpinnedAndRefreshAsync();
    }

    private void ToggleAutoStart(bool enabled)
    {
        // 设置变更事件会同步注册表与托盘勾选状态
        _services.Settings.SetAutoStart(enabled);
    }

    private async void CheckUpdateAsync()
    {
        _services.Tray.ShowBalloonTip("QuickClip", "正在检查更新…");
        var result = await _services.Update.CheckAndDownloadAsync(interactive: true);
        switch (result.Status)
        {
            case Services.UpdateCheckStatus.UpToDate:
                _services.Tray.ShowBalloonTip("QuickClip", result.Message ?? "已是最新版本");
                break;
            case Services.UpdateCheckStatus.Failed:
                _services.Tray.ShowBalloonTip("QuickClip", result.Message ?? "检查更新失败");
                break;
            case Services.UpdateCheckStatus.Ready:
            case Services.UpdateCheckStatus.UpdateAvailable:
                string extra = Services.UpdateService.CurrentChannel == Services.ReleaseChannel.Setup
                    ? "。点击托盘气泡或「立即更新」"
                    : "。点击托盘或「打开下载目录」，退出后自行替换";
                _services.Tray.ShowBalloonTip("QuickClip", result.Message + extra);
                break;
        }
    }

    private void ApplyPendingUpdate()
    {
        if (_services.Update.TryApplyPending(out string message, out bool shouldExit))
        {
            _services.Tray.ShowBalloonTip("QuickClip", message);
            if (shouldExit)
            {
                ExitApp();
            }

            return;
        }

        _services.Tray.ShowBalloonTip("QuickClip", message);
    }
}





