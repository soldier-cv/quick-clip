using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickClip.Models;
using Wpf.Ui.Controls;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QuickClip.Views;

/// <summary>
/// 轻量级应用内浮动 Toast 提示窗口：
/// 替代笨重的 Windows 系统气泡通知，提供自适应主题、平滑淡入淡出及紧凑尺寸。
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private bool _isClosing;
    private double _remainingSeconds = 1.8;

    public ToastWindow()
    {
        InitializeComponent();
        Opacity = 0;

        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_remainingSeconds)
        };
        _hideTimer.Tick += (_, _) => StartFadeOut();

        Loaded += (_, _) => Reposition();
    }

    public void ApplySize(ToastSize size)
    {
        switch (size)
        {
            case ToastSize.Small:
                RootCard.Width = 380;
                RootCard.Padding = new Thickness(14, 10, 14, 10);
                TitleText.FontSize = 12.5;
                MessageText.FontSize = 11.5;
                IconElement.FontSize = 18;
                IconElement.Margin = new Thickness(0, 0, 10, 0);
                break;
            case ToastSize.Large:
                RootCard.Width = 520;
                RootCard.Padding = new Thickness(18, 14, 18, 14);
                TitleText.FontSize = 14.5;
                MessageText.FontSize = 13;
                IconElement.FontSize = 22;
                IconElement.Margin = new Thickness(0, 0, 14, 0);
                break;
            default: // Medium（与主列表 460px 完全同宽）
                RootCard.Width = 460;
                RootCard.Padding = new Thickness(16, 12, 16, 12);
                TitleText.FontSize = 13.5;
                MessageText.FontSize = 12;
                IconElement.FontSize = 20;
                IconElement.Margin = new Thickness(0, 0, 12, 0);
                break;
        }

        UpdateLayout();
        Reposition();
    }

    public void UpdateContent(string title, string? message, SymbolRegular symbol, double durationSeconds = 1.8, ToastSize? size = null)
    {
        if (size.HasValue)
        {
            ApplySize(size.Value);
        }

        TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageText.Text = message;
            MessageText.Visibility = Visibility.Visible;
        }
        else
        {
            MessageText.Visibility = Visibility.Collapsed;
        }

        IconElement.Symbol = symbol;
        _remainingSeconds = Math.Max(0.8, durationSeconds);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(_remainingSeconds);
        _hideTimer.Start();

        UpdateLayout();
        Reposition();

        if (Opacity < 1 && !_isClosing)
        {
            StartFadeIn();
        }
    }

    public void Reposition()
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 12; // 与主面板右边距对齐

        double cardWidth = RootCard.ActualWidth > 0 ? RootCard.ActualWidth : RootCard.Width;
        if (double.IsNaN(cardWidth) || cardWidth <= 0) cardWidth = 460;

        // Window 外层有 Margin=12，所以 Window.Left = (卡片左边目标坐标) - 12
        // 卡片右侧与主面板右侧对齐：卡片右边 = workArea.Right - margin
        // 故卡片左边 = workArea.Right - margin - cardWidth
        Left = workArea.Right - margin - cardWidth - 12;

        // 底部固定在任务栏上方（呼吸间距 16px）
        Top = workArea.Bottom - ActualHeight - 4;
    }

    public void StartFadeIn()
    {
        _isClosing = false;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(OpacityProperty, fadeIn);
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideIn);
    }

    public void StartFadeOut()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _hideTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            Close();
        };

        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标悬停时暂停倒计时，方便阅读
        _hideTimer.Stop();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标移出后 1 秒内自动关闭
        if (!_isClosing)
        {
            _hideTimer.Interval = TimeSpan.FromSeconds(1.0);
            _hideTimer.Start();
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击立即关闭
        StartFadeOut();
    }
}
