using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Models;
using QuickClip.Services;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QuickClip.Views;

/// <summary>收集栈浮动 HUD 窗口：展示当前入栈项数与下个出栈预览，支持悬停展开队列清单。</summary>
public partial class StackHudWindow : Window
{
    public event Action? SplitLinesRequested;
    public event Action? ClearRequested;
    public event Action? ExitRequested;

    /// <summary>提供栈内前 N 项快照的委托。</summary>
    public Func<List<string>>? QueueSnapshotProvider { get; set; }

    private int _currentCount;
    private ToastSize _currentSize = ToastSize.Medium;
    private string? _lastRawPreview;
    private bool _detached;

    public StackHudWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_detached)
        {
            return;
        }

        Reposition();
    }

    public void ResetDock() => _detached = false;

    public bool IsDetached => _detached;

    public void Reposition()
    {
        ApplyPlacement(StackHudLayout.ParkBottomRight(WorkAreaRect(), StackHudLayout.DefaultPanelWidth, Height));
    }

    public void FollowPanel(Window? panel)
    {
        if (_detached)
        {
            ApplyWidth(panel?.ActualWidth > 1 ? panel.ActualWidth : StackHudLayout.DefaultPanelWidth);
            return;
        }

        if (panel is { IsVisible: true, ActualWidth: > 1, ActualHeight: > 1 } &&
            panel.Left > -10000 && panel.Top > -10000)
        {
            var place = StackHudLayout.DockToPanel(
                new StackHudLayout.RectD(panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight),
                WorkAreaRect(panel),
                Height);
            ApplyPlacement(place);
            return;
        }

        double panelWidth = panel is { ActualWidth: > 1 }
            ? panel.ActualWidth
            : StackHudLayout.DefaultPanelWidth;
        ApplyPlacement(StackHudLayout.ParkBottomRight(WorkAreaRect(panel), panelWidth, Height));
    }

    public void ApplySize(ToastSize size)
    {
        _currentSize = size;
        Height = 76;
        HudHeaderIcon.FontSize = 15;
        HudTitleText.FontSize = 13;
        CountBadge.FontSize = 11;

        SplitButton.Height = 24;
        SplitButton.FontSize = 11.5;

        ClearButton.Height = 24;
        ClearButton.FontSize = 11.5;

        ExitButton.Height = 24;
        ExitButton.Padding = new Thickness(6, 2, 6, 2);
        ApplyCompactChrome(Math.Max(1, Width - StackHudLayout.Chrome * 2));

        NextItemPreview.FontSize = 12;
        FormatNextItemPreview();
    }

    private void ApplyWidth(double panelWidth)
    {
        double width = StackHudLayout.WindowWidthForPanel(panelWidth);
        Width = width;
        double visual = Math.Max(1, panelWidth);
        PopupBorder.MinWidth = visual;
        PopupBorder.MaxWidth = visual;
        ApplyCompactChrome(visual);
    }

    private void ApplyPlacement(StackHudLayout.Placement place)
    {
        Width = place.Width;
        Height = place.Height;
        Left = place.Left;
        Top = place.Top;
        QueuePreviewPopup.Placement = place.PopupAbove
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        QueuePreviewPopup.VerticalOffset = place.PopupAbove ? -6 : 6;
        double visual = Math.Max(1, place.Width - StackHudLayout.Chrome * 2);
        PopupBorder.MinWidth = visual;
        PopupBorder.MaxWidth = visual;
        ApplyCompactChrome(visual);
    }

    private void ApplyCompactChrome(double visualWidth)
    {
        bool compact = visualWidth < 420;
        SplitButtonLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ClearButtonLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CountBadgeBorder.Visibility = compact && visualWidth < 300 ? Visibility.Collapsed : Visibility.Visible;
        SplitButton.Padding = compact ? new Thickness(6, 2, 6, 2) : new Thickness(8, 2, 8, 2);
        ClearButton.Padding = compact ? new Thickness(6, 2, 6, 2) : new Thickness(8, 2, 8, 2);
        SplitButtonIcon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 3, 0);
        ClearButtonIcon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 3, 0);
        SplitButton.Margin = new Thickness(0);
        ClearButton.Margin = compact ? new Thickness(4, 0, 0, 0) : new Thickness(6, 0, 0, 0);
        ExitButton.Margin = compact ? new Thickness(4, 0, 0, 0) : new Thickness(6, 0, 0, 0);
        ContentGrid.Margin = compact ? new Thickness(10, 8, 10, 8) : new Thickness(14, 9, 14, 8);
    }

    private static StackHudLayout.RectD WorkAreaRect(Window? relative = null)
    {
        if (relative != null)
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(relative).EnsureHandle();
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                var wa = screen.WorkingArea;
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                var toDip = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
                var topLeft = toDip.Transform(new System.Windows.Point(wa.Left, wa.Top));
                var bottomRight = toDip.Transform(new System.Windows.Point(wa.Right, wa.Bottom));
                return new StackHudLayout.RectD(
                    topLeft.X,
                    topLeft.Y,
                    bottomRight.X - topLeft.X,
                    bottomRight.Y - topLeft.Y);
            }
            catch
            {
                // 回退主显示器工作区
            }
        }

        var area = SystemParameters.WorkArea;
        return new StackHudLayout.RectD(area.Left, area.Top, area.Width, area.Height);
    }

    private DispatcherTimer? _transientFeedbackTimer;

    /// <summary>
    /// 在浮窗预览区就地显示轻量临时反馈（如「已拆分入栈 (N 项)」），并在短暂展示后恢复出栈预览，
    /// 避免弹出独立 Toast 遮挡视线或打断用户操作。
    /// </summary>
    public void ShowTransientFeedback(string feedbackText, double seconds = 1.2)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowTransientFeedback(feedbackText, seconds));
            return;
        }

        _transientFeedbackTimer?.Stop();
        NextItemPreview.Text = feedbackText;
        NextItemPreview.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");

        _transientFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds)
        };
        _transientFeedbackTimer.Tick += (_, _) =>
        {
            _transientFeedbackTimer.Stop();
            _transientFeedbackTimer = null;
            NextItemPreview.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            FormatNextItemPreview();
        };
        _transientFeedbackTimer.Start();
    }

    private void FormatNextItemPreview()
    {
        if (_transientFeedbackTimer != null)
        {
            return;
        }

        if (_currentCount == 0)
        {
            NextItemPreview.Text = "当前栈为空，复制内容将自动入栈";
            return;
        }

        string preview = _lastRawPreview?.Replace("\r", " ").Replace("\n", " ").Trim() ?? string.Empty;
        const int maxChars = 42;
        if (preview.Length > maxChars)
        {
            preview = preview[..maxChars] + "…";
        }
        NextItemPreview.Text = $"下一个出栈: {preview}";
    }

    public void UpdateState(int count, string? nextItemPreview)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateState(count, nextItemPreview));
            return;
        }

        _currentCount = count;
        _lastRawPreview = nextItemPreview;
        CountBadge.Text = $"{count} 项";
        FormatNextItemPreview();

        // 若当前预览浮层正好打开，实时刷新其内容
        if (QueuePreviewPopup.IsOpen)
        {
            RefreshQueuePreview();
        }
    }

    private void RefreshQueuePreview()
    {
        QueueItemsPanel.Children.Clear();
        PopupCountText.Text = $"共 {_currentCount} 项（最多显示 10 项）";

        var items = QueueSnapshotProvider?.Invoke() ?? new List<string>();
        if (items.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "（当前栈为空，复制内容将自动入栈）",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4)
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            QueueItemsPanel.Children.Add(emptyText);
            PopupMoreText.Visibility = Visibility.Collapsed;
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            string text = items[i];
            bool isNext = (i == 0);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 序号/下个出栈徽章
            var badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            };

            var badgeText = new TextBlock
            {
                FontSize = 9.5,
                FontWeight = isNext ? FontWeights.SemiBold : FontWeights.Normal
            };

            if (isNext)
            {
                badgeBorder.SetResourceReference(Border.BackgroundProperty, "Theme.Accent");
                badgeText.Foreground = Brushes.White;
                badgeText.Text = "1（下个出栈）";
            }
            else
            {
                badgeBorder.SetResourceReference(Border.BackgroundProperty, "Theme.Search");
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                badgeText.Text = $"{i + 1}";
            }

            badgeBorder.Child = badgeText;
            Grid.SetColumn(badgeBorder, 0);
            row.Children.Add(badgeBorder);

            // 内容预览单行截断
            var contentText = new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            contentText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
            Grid.SetColumn(contentText, 1);
            row.Children.Add(contentText);

            QueueItemsPanel.Children.Add(row);
        }

        if (_currentCount > items.Count)
        {
            PopupMoreText.Text = $"… 还有 {_currentCount - items.Count} 项待粘贴";
            PopupMoreText.Visibility = Visibility.Visible;
        }
        else
        {
            PopupMoreText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHudMouseEnter(object sender, MouseEventArgs e)
    {
        RefreshQueuePreview();
        QueuePreviewPopup.IsOpen = true;
    }

    private void OnHudMouseLeave(object sender, MouseEventArgs e)
    {
        if (QueuePreviewPopup.IsMouseOver)
        {
            return;
        }

        QueuePreviewPopup.IsOpen = false;
    }

    private void OnActionButtonsMouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标移动到操作按钮区域时，关闭队列预览浮层，避免遮挡并保证按钮操作绝对顺畅
        QueuePreviewPopup.IsOpen = false;
    }

    private void OnPopupMouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标移入浮层本身时保持展开
    }

    private void OnPopupMouseLeave(object sender, MouseEventArgs e)
    {
        QueuePreviewPopup.IsOpen = false;
    }

    private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            _detached = true;
        }
        catch
        {
            // 忽略
        }

        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnSplitLinesClicked(object sender, RoutedEventArgs e)
    {
        SplitLinesRequested?.Invoke();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        ClearRequested?.Invoke();
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
    }
}
