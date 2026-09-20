using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Models;
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

    private readonly DispatcherTimer _popupCloseTimer;
    private int _currentCount;
    private ToastSize _currentSize = ToastSize.Medium;
    private string? _lastRawPreview;

    public StackHudWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _popupCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _popupCloseTimer.Tick += (_, _) =>
        {
            _popupCloseTimer.Stop();
            QueuePreviewPopup.IsOpen = false;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reposition();
    }

    public void Reposition()
    {
        var workArea = SystemParameters.WorkArea;
        // Window 宽度为 484（含外层 Margin 12），内部 MainBorder 宽度为 460
        // Left = workArea.Right - Width 使 MainBorder 右边缘正好距工作区右侧 12px，与主列表完全对齐
        Left = workArea.Right - Width;
        // Top 底部距任务栏上方 16px 呼吸间距（含外层 Margin 12）
        Top = workArea.Bottom - Height - 4;
    }

    public void ApplySize(ToastSize size)
    {
        _currentSize = size;
        // 统一固定为 460px 宽度（外层含 Margin 12 即 484px，内部卡片 460px，与主面板及 Toast 像素级对齐）
        Width = 484;
        Height = 92;
        ContentGrid.Margin = new Thickness(14, 9, 14, 9);
        HudHeaderIcon.FontSize = 15;
        HudTitleText.FontSize = 13;
        CountBadge.FontSize = 11;

        SplitButton.Height = 24;
        SplitButton.FontSize = 11.5;
        SplitButton.Padding = new Thickness(8, 2, 8, 2);

        ClearButton.Height = 24;
        ClearButton.FontSize = 11.5;
        ClearButton.Padding = new Thickness(8, 2, 8, 2);

        ExitButton.Height = 24;
        ExitButton.Padding = new Thickness(6, 2, 6, 2);

        NextItemPreview.FontSize = 12;

        PopupBorder.MinWidth = 460;
        PopupBorder.MaxWidth = 520;

        UpdateLayout();
        Reposition();
        FormatNextItemPreview();
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
        PopupCountText.Text = $"共 {_currentCount} 项 (最多显示 10 项)";

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
                badgeText.Text = "1 (下个出栈)";
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
        _popupCloseTimer.Stop();
        RefreshQueuePreview();
        QueuePreviewPopup.IsOpen = true;
    }

    private void OnHudMouseLeave(object sender, MouseEventArgs e)
    {
        _popupCloseTimer.Stop();
        _popupCloseTimer.Start();
    }

    private void OnPopupMouseEnter(object sender, MouseEventArgs e)
    {
        _popupCloseTimer.Stop();
    }

    private void OnPopupMouseLeave(object sender, MouseEventArgs e)
    {
        _popupCloseTimer.Stop();
        _popupCloseTimer.Start();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击按钮时不触发窗口拖拽
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<System.Windows.Controls.Button>(dep) != null)
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
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
