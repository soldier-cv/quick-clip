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
    private Views.SnippetQuickAdjustWindow? _activeQuickAdjustWindow;
    private bool _exiting;
    /// <summary>热键唤起后短时忽略 Deactivated，避免 Activate 被前台锁拒绝时立刻 Hide。</summary>
    private DateTime _suppressDeactivateUntil = DateTime.MinValue;
    private DispatcherTimer? _hotkeyTopmostTimer;

    /// <summary>自有模态对话框（确认框 / 另存为）打开期间为 true：它会让主窗失焦，不能当成「点到外部」隐藏面板。</summary>
    private bool _modalDialogOpen;

    /// <summary>本次 OCR 的原始识别结果：供「重置」恢复，编辑不改写它。</summary>
    private string _ocrOriginalText = string.Empty;

    /// <summary>删除按钮节流：双击/连点时的第二次点击不应再作用于（可能已复用的）卡片。</summary>
    private long _lastDeleteClickTicks;

    /// <summary>主窗口是否已完成静默预热或首次显示。</summary>
    private bool _isWarmedUp;

    /// <summary>刚唤起主窗口的时间戳（TickCount64），在短时间内忽略 Toggle 隐藏，避免用户因等待冷启动快速连按而刚弹出又被关掉。</summary>
    private long _suppressToggleHideUntilTicks;

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
        _services.ClipboardGuard.ToggleRequested += ToggleWindow;
        _services.Hotkey.PastePlainRequested += OnPastePlainRequested;
        _services.Tray.ToggleRequested += ToggleWindow;
        _services.Tray.ExitRequested += ExitApp;
        _services.Tray.SettingsRequested += OpenSettingsWindow;
        _services.Tray.AutoStartToggleRequested += ToggleAutoStart;
        _services.Tray.CheckUpdateRequested += CheckUpdateAsync;
        _services.Tray.InstallUpdateRequested += ApplyPendingUpdate;
        _services.Tray.OpenDataFolderRequested += OpenDataFolderFromTray;
        _services.Tray.ClearTodayHistoryRequested += ClearTodayFromTray;
        _services.Tray.CapturePauseToggleRequested += ToggleCapturePause;
        // 粘贴/复制失败：面板通常已隐藏，这里同时更新状态栏（面板重新打开时可见）
        _services.Paste.PasteFailed += OnPasteFailed;

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
            _services.Paste.PasteFailed -= OnPasteFailed;
            _services.Settings.Changed -= OnSettingsChanged;
            ThemeService.Changed -= OnThemeChanged;
        };
        OnSettingsChanged();
    }

    private void OnPasteFailed(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnPasteFailed(message));
            return;
        }

        _viewModel.StatusText = message;
    }

    private static readonly uint WmShowQuickClip = QuickClip.Native.NativeMethods.RegisterWindowMessage("QUICKCLIP_SHOW_WINDOW_MSG");
    private static readonly uint WmExitQuickClip = QuickClip.Native.NativeMethods.RegisterWindowMessage("QUICKCLIP_EXIT_INSTANCE_MSG");
    private static readonly uint WmTaskbarCreated = QuickClip.Native.NativeMethods.RegisterWindowMessage("TaskbarCreated");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == QuickClip.Native.NativeMethods.WM_ACTIVATE)
        {
            int wa = (int)(wParam.ToInt64() & 0xFFFF);
            if (wa != QuickClip.Native.NativeMethods.WA_INACTIVE && lParam != IntPtr.Zero)
            {
                _services.Paste.RememberTargetWindow(lParam);
            }
        }

        if (msg == (int)WmShowQuickClip)
        {
            ShowWindow();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == (int)WmExitQuickClip)
        {
            PrepareForSystemExit();
            ExitApp();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg is Native.NativeMethods.WM_QUERYENDSESSION or Native.NativeMethods.WM_ENDSESSION)
        {
            PrepareForSystemExit();
            return IntPtr.Zero;
        }

        if (msg == (int)WmTaskbarCreated)
        {
            DebugLog.Log("检测到 Explorer 重建 (TaskbarCreated)，自愈刷新托盘与热键");
            _services.Hotkey.RefreshHotkeys();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
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

        var accentMuted = WindowChromeHelper.CreateSolidBrush(p.AccentMuted);
        PreviewQrDecodePanel.Background = accentMuted;
        PreviewQrDecodeBar.Background = accent;
        PreviewQrDecodeIcon.Foreground = accent;
        PreviewQrDecodeLabel.Foreground = secondary;
        PreviewQrDecodeText.Foreground = accent;
        PreviewQrDecodeScroll.Background = System.Windows.Media.Brushes.Transparent;
        CopyPreviewQrButton.Foreground = accent;
    }

    /// <summary>窗口句柄创建后挂载剪贴板监听。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _services.Monitor.Attach(this);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    public void PrepareForSystemExit()
    {
        _exiting = true;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // 不要清掉热键唤起宽限：Activate 成功后 WPF 仍可能立刻再打 Deactivated。
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && DateTime.UtcNow >= _suppressDeactivateUntil)
        {
            ApplyTopmostState(handle);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // 热键唤起时前台锁常让 Activate 失败，WPF 会立刻打 Deactivated。
        // 宽限内不藏，否则用户只看到托盘、点图标之后 Win+V 才可用。
        if (DateTime.UtcNow < _suppressDeactivateUntil)
        {
            DebugLog.Log("忽略失焦隐藏（热键唤起宽限）");
            return;
        }

        // 失焦即隐：点到其他应用时隐藏（仿系统 Win+V）。
        // 打开设置窗 / 短语微调 / 短语编辑 / 自有模态对话框时主窗也会失焦，不能当「点到外部」——否则列表会被误收起。
        if (IsVisible && !_exiting && !_services.Settings.WindowAlwaysOnTop &&
            QrOverlay.Visibility == Visibility.Collapsed &&
            OcrOverlay.Visibility == Visibility.Collapsed &&
            !_modalDialogOpen &&
            !IsSubWindowOpen())
        {
            DebugLog.Log("失焦隐藏面板");
            HideWindow();
        }
    }

    /// <summary>检查 QuickClip 自身的子窗口是否正在显示，若有则主面板绝不自动隐藏。</summary>
    private bool IsSubWindowOpen() =>
        (_settingsWindow is { IsVisible: true }) ||
        (_activeQuickAdjustWindow is { IsVisible: true }) ||
        (_activeSnippetDialog is { IsVisible: true });

    /// <summary>
    /// 显示自有模态对话框：期间主窗会失焦，必须屏蔽「失焦即隐」，
    /// 否则点「保存到本地」「清空列表」时面板会在对话框后面消失。
    /// </summary>
    private T ShowOwnedModal<T>(Func<T> show)
    {
        _modalDialogOpen = true;
        try
        {
            return show();
        }
        finally
        {
            _modalDialogOpen = false;
            // 对话框关闭后把焦点还回面板（未置顶时）
            if (IsVisible)
            {
                Activate();
            }
        }
    }

    private void OnTitleBarCloseClicked(object sender, RoutedEventArgs e)
    {
        // 必须走 HideWindow：只 Hide() 会让 StaysOpen=True 的悬停浮层和置顶定时器留在屏幕上
        HideWindow();
    }

    /// <summary>唤起 / 切换窗口（可由键盘钩子或托盘触发）。</summary>
    private void ToggleWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ToggleWindow);
            return;
        }

        DebugLog.Log($"ToggleWindow 触发, IsVisible={IsVisible}, IsActive={IsActive}");
        if (IsVisible && IsActive)
        {
            // 防连击误关：在刚唤起的 450ms 保护期内，忽略 Toggle 隐藏，避免用户因等待冷启动连按而刚弹出又被关掉
            if (Environment.TickCount64 < _suppressToggleHideUntilTicks)
            {
                DebugLog.Log("忽略刚唤起保护期内的 Toggle 隐藏请求");
                return;
            }

            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// 开机自启动静默预热：在屏幕可见区域外触发一次布局测量与渲染管线初始化，
    /// 提前完成 XAML 视觉树构建、数据模板解析与 JIT 编译，
    /// 消除用户首次按 Win+V 时的冷启动延迟。
    /// </summary>
    public void WarmUp()
    {
        if (_isWarmedUp || IsVisible)
        {
            return;
        }

        _isWarmedUp = true;
        try
        {
            DebugLog.Log("开始执行主窗口静默预热");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            double oldLeft = Left;
            double oldTop = Top;
            bool oldShowActivated = ShowActivated;

            // 移到屏幕可视区域之外极远处，且不抢夺前台焦点
            ShowActivated = false;
            Left = -32000;
            Top = -32000;

            Show();
            UpdateLayout();

            Hide();

            // 还原属性与位置
            ShowActivated = oldShowActivated;
            Left = oldLeft;
            Top = oldTop;
            PositionWindow();

            sw.Stop();
            DebugLog.Log($"主窗口静默预热完成，耗时 {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("主窗口静默预热异常", ex);
        }
    }

    private void ShowWindow()
    {
        _suppressToggleHideUntilTicks = Environment.TickCount64 + 450;
        _isWarmedUp = true;

        // 记录唤起前的目标窗口，用于粘贴回填
        _services.Paste.RememberTargetWindow();
        PositionWindow();
        // 钩子/RegisterHotKey 唤起不算“进程收到用户输入”，前台锁会让 Activate 失败并立刻 Deactivated。
        // Activated 里曾经清掉宽限，导致约 1 秒后失焦把刚唤起的面板藏回托盘。
        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(1500);
        Show();
        Activate();

        // 每次打开都默认选中第 1 条（最近一条），Enter 即贴
        if (_viewModel.Items.Count > 0)
        {
            _viewModel.SelectedItem = _viewModel.Items[0];
            ItemList.ScrollIntoView(_viewModel.Items[0]);
        }

        // 面板可见期间新捕获不抢占选中项（见 MainViewModel.SuppressAutoSelect）
        _viewModel.SuppressAutoSelect = true;

        SearchBox.Focus();
        SearchBox.SelectAll();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            const uint flags = QuickClip.Native.NativeMethods.SWP_NOMOVE |
                               QuickClip.Native.NativeMethods.SWP_NOSIZE |
                               QuickClip.Native.NativeMethods.SWP_SHOWWINDOW;
            QuickClip.Native.NativeMethods.SetWindowPos(
                handle, QuickClip.Native.NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
            bool foreground = QuickClip.Native.NativeMethods.ForceForeground(handle);
            DebugLog.Log($"热键唤起 ForceForeground={foreground}，宽限内保持 TOPMOST");
            ScheduleHotkeyTopmostDemote();
        }

        DebugLog.Log("窗口已显示");
    }

    /// <summary>宽限结束后按「窗口置顶」设置恢复 z-order，避免一直抢在所有窗口之上。</summary>
    private void ScheduleHotkeyTopmostDemote()
    {
        _hotkeyTopmostTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _hotkeyTopmostTimer.Tick -= OnHotkeyTopmostDemote;
        _hotkeyTopmostTimer.Tick += OnHotkeyTopmostDemote;
        _hotkeyTopmostTimer.Stop();
        _hotkeyTopmostTimer.Start();
    }

    private void OnHotkeyTopmostDemote(object? sender, EventArgs e)
    {
        _hotkeyTopmostTimer?.Stop();
        if (!IsVisible)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            ApplyTopmostState(handle);
        }
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
        _hotkeyTopmostTimer?.Stop();
        PreviewPopup.IsOpen = false;
        _viewModel.SuppressAutoSelect = false;
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
            // OCR 页可直接编辑：Esc 也当关闭，但改动过就先确认，避免手滑丢掉编辑
            if (key == Key.Escape || settings.HidePanelHotkey.Matches(key, modifiers))
            {
                OnCloseOverlays(sender, e);
                e.Handled = true;
            }

            return;
        }

        // 焦点在搜索框时：粘贴类快捷键仍然可用（Win+V → 输入 → Enter 是主流程），
        // 但删除 / 置顶等破坏性动作必须屏蔽，否则会一边打字一边删掉历史条目。
        bool typing = IsSearchFocused();

        if (settings.HidePanelHotkey.Matches(key, modifiers))
        {
            HideWindow();
            e.Handled = true;
            return;
        }

        if (settings.PasteSelectedPlainHotkey.Matches(key, modifiers))
        {
            // 长按不重复粘贴
            if (!e.IsRepeat)
            {
                PasteSelected(plainOnly: true);
            }

            e.Handled = true;
            return;
        }

        if (settings.PasteSelectedHotkey.Matches(key, modifiers))
        {
            if (!e.IsRepeat)
            {
                PasteSelected(plainOnly: false);
            }

            e.Handled = true;
            return;
        }

        if (settings.DeleteSelectedHotkey.Matches(key, modifiers))
        {
            if (!typing && !e.IsRepeat)
            {
                _ = _viewModel.DeleteSelectedAsync();
            }

            e.Handled = true;
            return;
        }

        if (settings.TogglePinHotkey.Matches(key, modifiers))
        {
            if (!typing && !e.IsRepeat)
            {
                ToggleWindowPin();
            }

            e.Handled = true;
            return;
        }

        if (settings.CopySelectedHotkey.Matches(key, modifiers) && !typing)
        {
            _ = _viewModel.CopySelectedToClipboard();
            e.Handled = true;
            return;
        }

        // 方向键（当焦点不在搜索框时，由窗口级 KeyDown 处理；焦点在搜索框时由 SearchBox_PreviewKeyDown 优先处理）
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

        // 开启/关闭收集栈
        if (settings.StartStackHotkey.Matches(key, modifiers))
        {
            if (!e.IsRepeat)
            {
                _viewModel.ToggleStackMode();
                if (!settings.WindowAlwaysOnTop)
                {
                    HideWindow();
                }
            }

            e.Handled = true;
            return;
        }

        // 1~9 / 小键盘：固定快速粘贴
        if (modifiers == ModifierKeys.None && !typing)
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

    private bool IsSearchFocused() => SearchBox.IsKeyboardFocusWithin;

    /// <summary>
    /// 焦点在搜索框时，TextBox 会在 KeyDown 冒泡到窗口前把 ↑/↓ 吞掉（内部标记 Handled），
    /// 导致窗口 KeyDown 收不到方向键、列表选中无法移动。这里直接在 SearchBox 自身的 PreviewKeyDown
    /// 隧道阶段（先于控件内部处理）拦截方向键并移动列表选中；普通字符不匹配，仍正常输入搜索框。
    /// 直接挂载在 SearchBox 上，确保绝不影响 OCR 识别多行文本框等其他控件的上下光标移动。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        key = Models.HotkeyBinding.NormalizeKey(key);
        var modifiers = Keyboard.Modifiers &
                        (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        var settings = _services.Settings;

        if (settings.MoveDownHotkey.Matches(key, modifiers))
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (settings.MoveUpHotkey.Matches(key, modifiers))
        {
            MoveSelection(-1);
            e.Handled = true;
        }
    }

    private void PasteItemAt(int index)
    {
        if (_viewModel.IsSnippetsTabActive)
        {
            int snippetIndex = index - 1;
            if (snippetIndex >= 0 && snippetIndex < _viewModel.Snippets.Count)
            {
                _viewModel.SelectedSnippet = _viewModel.Snippets[snippetIndex];
                PasteSelected(false);
            }
            return;
        }

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
        // 置顶（Ctrl+P / 图钉）= 工作会话：失焦不藏、粘贴后也不关；未置顶且未开启连续粘贴则粘贴后隐藏
        bool pinned = _services.Settings.WindowAlwaysOnTop;
        bool continuous = _services.Settings.ContinuousPasteMode;
        if (!pinned && !continuous)
        {
            HideWindow();
        }

        if (_viewModel.IsSnippetsTabActive)
        {
            if (_viewModel.SelectedSnippet != null)
            {
                _ = _viewModel.PasteSnippetAsync(_viewModel.SelectedSnippet, plainOnly);
                if (pinned)
                {
                    _viewModel.StatusText = "已粘贴短语（置顶中，面板保持打开）";
                }
                else if (continuous)
                {
                    _viewModel.StatusText = "已粘贴短语（连续粘贴中）";
                }
            }

            if (continuous)
            {
                ReactivatePanelForContinuousPaste();
            }
            return;
        }

        _viewModel.PasteSelected(plainOnly);
        if (pinned)
        {
            _viewModel.StatusText = "已粘贴（置顶中，面板保持打开）";
        }
        else if (continuous)
        {
            _viewModel.StatusText = "已粘贴（连续粘贴中）";
        }

        if (continuous)
        {
            ReactivatePanelForContinuousPaste();
        }
    }

    /// <summary>连续粘贴模式：完成一次粘贴后重新激活主面板并保留焦点，以便继续按 Enter 或上下键顺次粘贴。</summary>
    private void ReactivatePanelForContinuousPaste()
    {
        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(600);
        Task.Delay(150).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (IsVisible)
                {
                    Activate();
                    Focus();
                }
            });
        });
    }

    private void MoveSelection(int delta)
    {
        if (_viewModel.IsSnippetsTabActive)
        {
            if (_viewModel.Snippets.Count == 0) return;
            int current = _viewModel.SelectedSnippet == null
                ? -1
                : _viewModel.Snippets.IndexOf(_viewModel.SelectedSnippet);
            int target;
            if (current == -1)
            {
                target = delta > 0 ? 0 : _viewModel.Snippets.Count - 1;
            }
            else
            {
                target = Math.Clamp(current + delta, 0, _viewModel.Snippets.Count - 1);
            }
            _viewModel.SelectedSnippet = _viewModel.Snippets[target];
            SnippetList.ScrollIntoView(_viewModel.SelectedSnippet);
            return;
        }

        if (_viewModel.Items.Count == 0)
        {
            return;
        }

        int currentHist = _viewModel.SelectedItem == null
            ? 0
            : _viewModel.Items.IndexOf(_viewModel.SelectedItem);
        int targetHist = Math.Clamp(currentHist + delta, 0, _viewModel.Items.Count - 1);
        _viewModel.SelectedItem = _viewModel.Items[targetHist];
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
        PreviewTypeIcon.ClearValue(Wpf.Ui.Controls.SymbolIcon.SymbolProperty);
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
            PreviewQrDecodePanel.Visibility = Visibility.Collapsed;
            PreviewQrPanel.Visibility = Visibility.Visible;
            return;
        }

        PreviewQrPanel.Visibility = Visibility.Collapsed;
        PreviewQrImage.Source = null;
        SetQrCaption(null);
        PreviewHoverImage.ClearValue(UIElement.VisibilityProperty);
        PreviewScroll.ClearValue(UIElement.VisibilityProperty);
        // 解码绿块不能靠 HasQr 绑定：Popup 里绑定失败或 ClearValue 后会回默认 Visible，任何预览都带一块绿
        PreviewQrDecodePanel.Visibility = ShouldShowQrDecodePanel()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool ShouldShowQrDecodePanel() =>
        !_previewQrMode &&
        PreviewPopup.DataContext is ClipboardItemViewModel { IsImage: true, HasQr: true };

    /// <summary>生成预览只在失败/进行中显示状态，成功后不把原文铺在码下面。</summary>
    private void SetQrCaption(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            PreviewQrCaption.Text = string.Empty;
            PreviewQrCaption.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewQrCaption.Text = text;
        PreviewQrCaption.Visibility = Visibility.Visible;
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
        SetQrCaption("生成中…");

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
                SetQrCaption("当前条目无法生成二维码");
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
            SetQrCaption(null);
            PreviewTypeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.QrCode24;
            RepositionPreview();
            _ = Dispatcher.BeginInvoke(RepositionPreview, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("悬停生成二维码失败", ex);
            if (gen == _previewQrGeneration && _previewQrMode)
            {
                SetQrCaption("二维码生成失败");
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
            PreviewTypeIcon.ClearValue(Wpf.Ui.Controls.SymbolIcon.SymbolProperty);
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

    private void OnCopyPreviewQrClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        string? text = (PreviewPopup.DataContext as ClipboardItemViewModel)?.QrText;
        if (string.IsNullOrEmpty(text))
        {
            text = PreviewQrDecodeText.Text;
        }

        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                _services.Toast.Show("已复制二维码文本", text, Wpf.Ui.Controls.SymbolRegular.Copy24);
                _viewModel.StatusText = "已复制二维码文本";
            }
            catch (Exception ex)
            {
                DebugLog.LogException("复制二维码文本失败", ex);
            }
        }
    }

    private void OnPreviewQrDecodePanelMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            PastePreviewQrContent();
        }
    }

    private void PastePreviewQrContent()
    {
        string? text = (PreviewPopup.DataContext as ClipboardItemViewModel)?.QrText;
        if (string.IsNullOrEmpty(text))
        {
            text = PreviewQrDecodeText.Text;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 双击粘贴时关闭悬停预览浮层
        PreviewPopup.IsOpen = false;

        bool pinned = _services.Settings.WindowAlwaysOnTop;
        bool continuous = _services.Settings.ContinuousPasteMode;

        if (!pinned && !continuous)
        {
            HideWindow();
        }

        _services.Paste.RememberTargetWindow();
        _services.Paste.PasteText(text, plainOnly: false);

        if (pinned)
        {
            _viewModel.StatusText = "已粘贴二维码文本（置顶中，面板保持打开）";
        }
        else if (continuous)
        {
            _viewModel.StatusText = "已粘贴二维码文本（连续粘贴中）";
            ReactivatePanelForContinuousPaste();
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.CopySelectedToClipboard();
        }
    }

    private void OnCopyFilePathClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            _ = _viewModel.CopyFilePathAsync(vm);
        }
    }

    private void OnOcrClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (GetCardViewModel(sender) is not { } vm || vm.IsOcrBusy)
        {
            return;
        }

        _viewModel.SelectedItem = vm;
        _ = _viewModel.OcrSelectedAsync(vm);
    }

    /// <summary>保存选中图片到本地（另存为对话框 + 复制预览 PNG）。</summary>
    private void OnSaveImageClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is not { } vm)
        {
            return;
        }

        _viewModel.SelectedItem = vm;
        string? previewPath = vm.Item.PreviewPath;
        if (!vm.IsImage || string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
        {
            _viewModel.StatusText = "图片文件已不存在，无法保存";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存图片",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*",
            FileName = $"QuickClip_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            DefaultExt = ".png"
        };

        // 模态对话框期间主窗失焦，不能让「失焦即隐」把面板藏到对话框后面
        bool confirmed = ShowOwnedModal(() => dialog.ShowDialog(this) == true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            File.Copy(previewPath, dialog.FileName, true);
            _viewModel.StatusText = $"已保存: {dialog.FileName}";
            DebugLog.Log($"图片已保存到本地: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("保存图片失败", ex);
            _viewModel.StatusText = "保存失败，请检查目标路径权限";
        }
    }

    private async void OnPinClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            await _viewModel.TogglePinSelectedAsync(vm);
            if (_viewModel.SelectedItem != null)
            {
                ItemList.ScrollIntoView(_viewModel.SelectedItem);
            }
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is not { } vm)
        {
            return;
        }

        // 双击/连点删除按钮：第二次点击时卡片可能已被虚拟化容器复用（DataContext 变成相邻条目），
        // 因此除了 VM 内部按 id 的重入保护，这里再做一次短时抖动过滤。
        long now = Environment.TickCount64;
        if (now - _lastDeleteClickTicks < 400)
        {
            DebugLog.LogDetail("忽略删除按钮的重复点击");
            return;
        }

        _lastDeleteClickTicks = now;
        _viewModel.SelectedItem = vm;
        _ = _viewModel.DeleteSelectedAsync();
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
        QrTextBlock.Text = string.Empty;
        QrOverlay.Visibility = Visibility.Visible;
        QrOverlay.IsHitTestVisible = true;
    }

    private void ShowOcrOverlay(string title, string text)
    {
        OcrTitle.Text = string.IsNullOrWhiteSpace(title) ? "离线识别" : title;
        _ocrOriginalText = text ?? string.Empty;
        OcrText.Text = _ocrOriginalText;
        OcrResetButton.IsEnabled = false; // 刚打开时与原文一致，按钮置灰；后续按编辑状态更新
        OcrOverlay.Visibility = Visibility.Visible;
        OcrOverlay.IsHitTestVisible = true;
        // 结果页支持简易编辑：直接落焦并把光标放到末尾，方便接着改错字
        OcrText.Focus();
        OcrText.CaretIndex = OcrText.Text.Length;
    }

    /// <summary>编辑后把「重置」按钮在可用 / 置灰间切换，提示当前是否已偏离原始识别结果。</summary>
    private void UpdateOcrResetState() =>
        OcrResetButton.IsEnabled = !string.Equals(OcrText.Text, _ocrOriginalText, StringComparison.Ordinal);

    private void OnOcrTextChanged(object sender, TextChangedEventArgs e) => UpdateOcrResetState();

    private void OnResetOcrClicked(object sender, RoutedEventArgs e)
    {
        OcrText.Text = _ocrOriginalText;
        OcrText.CaretIndex = OcrText.Text.Length;
        UpdateOcrResetState();
        _viewModel.StatusText = "已还原识别结果";
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
        // 编辑过 OCR 文字时先确认，避免误关丢掉手工修正的内容
        if (OcrOverlay.Visibility == Visibility.Visible &&
            !string.Equals(OcrText.Text, _ocrOriginalText, StringComparison.Ordinal))
        {
            var result = ShowOwnedModal(() => System.Windows.MessageBox.Show(
                this,
                "识别结果已修改，关闭后修改内容将丢失。\n确定关闭？",
                "关闭 OCR 结果",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No));

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                // 取消关闭后把焦点还给可编辑框，接着改字
                OcrText.Focus();
                return;
            }
        }

        QrOverlay.Visibility = Visibility.Collapsed;
        OcrOverlay.Visibility = Visibility.Collapsed;
        // 弹层收起后把键盘焦点还给搜索框：焦点若留在已折叠的 OcrText 上，
        // 用户得先点一下面板才能继续打字检索
        SearchBox.Focus();
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
        UpdateCaptureBadges();
    }

    /// <summary>同步「已暂停捕获 / 仅文本」状态徽章：捕获被限制时必须可见。</summary>
    private void UpdateCaptureBadges()
    {
        CapturePausedBadge.Visibility = _services.Settings.CapturePaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        TextOnlyBadge.Visibility = _services.Settings.TextOnlyCapture
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>托盘「暂停捕获」勾选：暂停后不再记录任何剪贴板内容。</summary>
    private void ToggleCapturePause(bool paused)
    {
        _services.SetCapturePaused(paused);
        UpdateCaptureBadges();
        _viewModel.StatusText = paused ? "已暂停捕获" : "已恢复捕获";
        _services.Tray.ShowBalloonTip(
            "QuickClip",
            paused ? "已暂停捕获剪贴板，复制的内容不会进入历史" : "已恢复捕获剪贴板");
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

        string stackTip = $"[{s.StartStackHotkey}] 开启/关闭收集栈\n";

        string body =
            $"单击选中 · 双击粘贴 · 卡片「复制」或 [{s.CopySelectedHotkey}] 仅复制\n" +
            $"[{s.PasteSelectedHotkey}] 粘贴选中项\n" +
            $"[{s.PasteSelectedPlainHotkey}] 纯文本粘贴选中项\n" +
            $"[1 ~ 9] 快速粘贴第 1~9 条\n" +
            $"{globalPaste}\n" +
            $"{stackTip}" +
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
        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(500);
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
        // 托盘菜单直接点击即删，风险高：与面板内「清空列表」一样先确认
        var result = ShowOwnedModal(() => System.Windows.MessageBox.Show(
            this,
            "将删除今天的全部非置顶历史记录，置顶条目会保留。\n确定清除今日历史？",
            "清除今日历史",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No));

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.ClearTodayAndRefreshAsync();
        _services.Tray.ShowBalloonTip("QuickClip", _viewModel.StatusText);
    }

    /// <summary>列表工具栏：清空全部非置顶历史（置顶保留）。</summary>
    private async void OnClearListClicked(object sender, RoutedEventArgs e)
    {
        var result = ShowOwnedModal(() => System.Windows.MessageBox.Show(
            this,
            "将删除全部非置顶历史记录，置顶条目会保留。\n确定清空列表？",
            "清空列表",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No));

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
                _services.Tray.ShowBalloonTip(
                    "QuickClip",
                    (result.Message ?? "已下载新版本") + "。点击「立即更新」安装，或下次手动启动自动安装");
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

    // ---------- 收集栈、常用短语与翻译动作 ----------

    private void OnStackModeClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleStackMode();
    }

    private void OnPinClipboardToDesktopClicked(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.PinCurrentClipboardToDesktop())
        {
            _viewModel.StatusText = "当前剪贴板没有可贴图的内容";
        }
    }

    private void OnHistoryTabClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedMainTab = 0;
    }

    private void OnSnippetsTabClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedMainTab = 1;
    }

    private void OnCardStickyClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            if (!_viewModel.PinItemToDesktop(vm))
            {
                _viewModel.StatusText = "该条目没有可贴图的内容";
            }
        }
    }

    private async void OnCardTranslateClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            _viewModel.SelectedItem = vm;
            await _viewModel.ToggleTranslateItemAsync(vm);
        }
    }

    private async void OnCopyTranslationClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm && !string.IsNullOrWhiteSpace(vm.TranslatedText))
        {
            await _services.Paste.CopyTextAsync(vm.TranslatedText, plainOnly: true);
            _viewModel.StatusText = "已复制译文";
        }
    }

    private void OnPasteTranslationClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm && !string.IsNullOrWhiteSpace(vm.TranslatedText))
        {
            bool pinned = _services.Settings.WindowAlwaysOnTop;
            if (!pinned)
            {
                HideWindow();
            }

            _services.Paste.RememberTargetWindow();
            _services.Paste.PasteText(vm.TranslatedText, plainOnly: false);
        }
    }

    private void OnCollapseTranslationClicked(object sender, RoutedEventArgs e)
    {
        if (GetCardViewModel(sender) is { } vm)
        {
            vm.IsTranslated = false;
        }
    }

    private void OnMenuPasteClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) != null)
        {
            PasteSelected(plainOnly: false);
        }
    }

    private void OnMenuPastePlainClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) != null)
        {
            PasteSelected(plainOnly: true);
        }
    }

    private void OnMenuCopyClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) != null)
        {
            _ = _viewModel.CopySelectedToClipboard();
        }
    }

    private void OnMenuSaveAsSnippetClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is not { } vm || string.IsNullOrWhiteSpace(vm.Item.TextContent))
        {
            return;
        }

        string raw = vm.Item.TextContent.Trim();
        string title = raw.Length > 20 ? raw.Substring(0, 20) + "…" : raw;
        title = title.Replace("\r", " ").Replace("\n", " ");
        var snippet = new Models.SnippetItem
        {
            Title = title,
            Category = "常用",
            Content = raw
        };

        var dialog = new Views.SnippetEditWindow(snippet, this);
        bool confirmed = ShowOwnedModal(() => dialog.ShowDialog() == true);
        if (confirmed)
        {
            _ = _viewModel.AddSnippetAsync(dialog.ResultItem);
            _viewModel.StatusText = "已保存为常用短语";
        }
    }

    private void OnMenuStickyClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { ShowStickyAction: true } vm)
        {
            _viewModel.PinItemToDesktop(vm);
        }
    }

    private void OnMenuPushStackClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } vm)
        {
            _viewModel.PushItemToStack(vm);
            _viewModel.StatusText = $"已压入收集栈 (当前共 {_viewModel.StackCount} 项)";
        }
    }

    private async void OnMenuTranslateClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { ShowTranslateAction: true } vm)
        {
            await _viewModel.ToggleTranslateItemAsync(vm);
        }
    }

    private async void OnMenuPinClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } vm)
        {
            await _viewModel.TogglePinSelectedAsync(vm);
        }
    }

    private void OnMenuDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) != null)
        {
            _ = _viewModel.DeleteSelectedAsync();
        }
    }

    /// <summary>右键菜单动作前先选中被右键的卡片，避免误操作到上一次选中项。</summary>
    private ClipboardItemViewModel? ResolveMenuTarget(object sender)
    {
        var vm = GetCardViewModel(sender);
        if (vm != null)
        {
            _viewModel.SelectedItem = vm;
            ItemList.ScrollIntoView(vm);
        }

        return vm ?? _viewModel.SelectedItem;
    }

    private Views.SnippetEditWindow? _activeSnippetDialog;

    private void OnAddSnippetClicked(object sender, RoutedEventArgs e)
    {
        if (_activeSnippetDialog != null)
        {
            _activeSnippetDialog.Activate();
            return;
        }

        var dialog = new Views.SnippetEditWindow(null);
        _activeSnippetDialog = dialog;
        dialog.Saved += async item =>
        {
            await _viewModel.AddSnippetAsync(item);
            _viewModel.StatusText = "已添加常用短语";
        };
        dialog.Closed += (_, _) =>
        {
            if (_activeSnippetDialog == dialog)
            {
                _activeSnippetDialog = null;
            }
        };
        dialog.Show();
    }

    private void OnEditSnippetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            OpenSnippetEditor(snippet);
        }
    }

    private void OpenSnippetEditor(Models.SnippetItem snippet)
    {
        if (_activeSnippetDialog != null)
        {
            _activeSnippetDialog.Activate();
            return;
        }

        var dialog = new Views.SnippetEditWindow(snippet);
        _activeSnippetDialog = dialog;
        dialog.Saved += async item =>
        {
            await _viewModel.UpdateSnippetAsync(item);
            _viewModel.StatusText = "已更新常用短语";
        };
        dialog.Closed += (_, _) =>
        {
            if (_activeSnippetDialog == dialog)
            {
                _activeSnippetDialog = null;
            }
        };
        dialog.Show();
    }

    private void OnSnippetQuickAdjustClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            OpenSnippetQuickAdjust(snippet);
        }
    }

    private void OpenSnippetQuickAdjust(Models.SnippetItem snippet)
    {
        if (snippet == null) return;

        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(500);

        if (_activeQuickAdjustWindow is { IsVisible: true })
        {
            _activeQuickAdjustWindow.UpdateSnippet(snippet);
            _activeQuickAdjustWindow.Activate();
            return;
        }

        var adjustWin = new Views.SnippetQuickAdjustWindow(snippet, this);
        _activeQuickAdjustWindow = adjustWin;

        adjustWin.Closed += (_, _) =>
        {
            if (_activeQuickAdjustWindow == adjustWin)
            {
                _activeQuickAdjustWindow = null;
            }
        };

        adjustWin.ApplyAndPasteRequested += (rawText, snip) =>
        {
            string? currentClip = null;
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    currentClip = System.Windows.Clipboard.GetText();
                }
            }
            catch { }

            string finalText = Models.SnippetItem.ResolveText(rawText, currentClip);

            bool pinned = _services.Settings.WindowAlwaysOnTop;
            bool continuous = _services.Settings.ContinuousPasteMode;

            if (!pinned && !continuous)
            {
                HideWindow();
            }

            _services.Paste.RememberTargetWindow();
            _services.Paste.PasteText(finalText, plainOnly: false);

            if (pinned)
            {
                _viewModel.StatusText = "已微调并粘贴（置顶中，面板保持打开）";
            }
            else if (continuous)
            {
                _viewModel.StatusText = "已微调并粘贴（连续粘贴中）";
                ReactivatePanelForContinuousPaste();
            }
        };

        adjustWin.CopyOnlyRequested += (rawText, snip) =>
        {
            try
            {
                string? currentClip = null;
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        currentClip = System.Windows.Clipboard.GetText();
                    }
                }
                catch { }

                string finalText = Models.SnippetItem.ResolveText(rawText, currentClip);
                System.Windows.Clipboard.SetText(finalText);
                _services.Toast.Show("已复制到剪贴板", "已复制微调后的短语内容", Wpf.Ui.Controls.SymbolRegular.Copy24);
                _viewModel.StatusText = "已复制微调后的短语内容";
            }
            catch (Exception ex)
            {
                DebugLog.LogException("复制微调短语失败", ex);
            }
        };

        adjustWin.Show();
    }

    private void OnMenuSnippetPasteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            _viewModel.SelectedSnippet = snippet;
            PasteSelected(plainOnly: false);
        }
    }

    private void OnMenuSnippetQuickAdjustClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            OpenSnippetQuickAdjust(snippet);
        }
    }

    private void OnMenuSnippetCopyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            _ = CopySnippetDirectAsync(snippet);
        }
    }

    private void OnMenuSnippetEditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            OpenSnippetEditor(snippet);
        }
    }

    private void OnMenuSnippetDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            _ = _viewModel.DeleteSnippetAsync(snippet);
            _viewModel.StatusText = "已删除常用短语";
        }
    }

    private void OnDeleteSnippetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            _ = _viewModel.DeleteSnippetAsync(snippet);
            _viewModel.StatusText = "已删除常用短语";
        }
    }

    private void OnPasteSnippetDirectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            bool pinned = _services.Settings.WindowAlwaysOnTop;
            if (!pinned)
            {
                HideWindow();
            }

            _ = _viewModel.PasteSnippetAsync(snippet, plainOnly: false);
        }
    }

    private void OnCopySnippetDirectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.SnippetItem snippet })
        {
            _ = CopySnippetDirectAsync(snippet);
        }
    }

    private async Task CopySnippetDirectAsync(Models.SnippetItem snippet)
    {
        string text = _viewModel.ResolveSnippetText(snippet);
        await _services.Paste.CopyTextAsync(text, plainOnly: true);
        _viewModel.StatusText = "已复制短语内容";
    }

    private void OnSnippetListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击到「粘贴/复制/编辑/删除」按钮上时，交给按钮的 Click，避免误触发粘贴
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        if (SnippetList.SelectedItem is Models.SnippetItem snippet)
        {
            bool pinned = _services.Settings.WindowAlwaysOnTop;
            if (!pinned)
            {
                HideWindow();
            }

            _ = _viewModel.PasteSnippetAsync(snippet, plainOnly: false);
        }
    }

    private async void OnTranslateOcrClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OcrText.Text))
        {
            return;
        }

        OcrTitle.Text = "正在翻译…";
        try
        {
            var res = await _services.Translation.TranslateAsync(OcrText.Text);
            if (res.Success)
            {
                OcrText.Text = OcrText.Text + "\r\n\r\n--- 翻译结果 (" + res.Engine + ") ---\r\n" + res.TranslatedText;
                OcrTitle.Text = "识别与翻译完成";
            }
            else
            {
                OcrTitle.Text = "翻译失败: " + res.ErrorMessage;
            }
        }
        catch (Exception ex)
        {
            OcrTitle.Text = "翻译异常: " + ex.Message;
        }
    }
}





