using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace QuickClip.Views;

/// <summary>
/// 桌面贴图/便签置顶浮窗：
/// - 支持图片贴图：拖拽平移、滚轮缩放、Ctrl+滚轮调透明度、双击关闭、右键菜单另存与复制；
/// - 支持文本便签：置顶便签、原地编辑与复制。
/// </summary>
public partial class StickyWindow : Window
{
    private double _zoomFactor = 1.0;
    private double _originalWidth;
    private double _originalHeight;
    private BitmapSource? _bitmapSource;
    private readonly DispatcherTimer _badgeTimer;

    public bool IsImageMode { get; private set; }

    public StickyWindow()
    {
        InitializeComponent();

        _badgeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _badgeTimer.Tick += (s, e) =>
        {
            _badgeTimer.Stop();
            ZoomBadge.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>以图片模式初始化贴图。</summary>
    public void InitializeImage(BitmapSource bitmap)
    {
        IsImageMode = true;
        _bitmapSource = bitmap;
        StickyImage.Source = bitmap;

        // 计算初始显示尺寸（限制在屏幕工作区的 60% 以内，避免超大图撑爆屏幕）
        double screenW = SystemParameters.WorkArea.Width * 0.6;
        double screenH = SystemParameters.WorkArea.Height * 0.6;

        double w = bitmap.PixelWidth;
        double h = bitmap.PixelHeight;

        if (w > screenW || h > screenH)
        {
            double scale = Math.Min(screenW / w, screenH / h);
            w *= scale;
            h *= scale;
        }

        _originalWidth = Math.Max(w, 120);
        _originalHeight = Math.Max(h, 90);

        StickyImage.Width = _originalWidth;
        StickyImage.Height = _originalHeight;

        ImageContainer.Visibility = Visibility.Visible;
        TextContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>以本地图片路径初始化贴图。</summary>
    public bool InitializeImagePath(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            InitializeImage(bitmap);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以文本便签模式初始化贴图。</summary>
    public void InitializeText(string text)
    {
        IsImageMode = false;
        StickyTextBox.Text = text ?? string.Empty;

        ImageContainer.Visibility = Visibility.Collapsed;
        TextContainer.Visibility = Visibility.Visible;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略非正常拖拽状态
            }
        }
    }

    private void OnImageContainerDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl + 滚轮：调节不透明度 (20% ~ 100%)
            double newOpacity = Opacity + (e.Delta > 0 ? 0.05 : -0.05);
            Opacity = Math.Clamp(newOpacity, 0.2, 1.0);
            ShowBadge($"不透明度: {(int)(Opacity * 100)}%");
            e.Handled = true;
            return;
        }

        if (IsImageMode && _bitmapSource != null)
        {
            // 滚轮：缩放图片 (20% ~ 400%)
            double scaleStep = e.Delta > 0 ? 1.15 : 0.85;
            double newZoom = _zoomFactor * scaleStep;
            newZoom = Math.Clamp(newZoom, 0.2, 4.0);

            _zoomFactor = newZoom;
            StickyImage.Width = _originalWidth * _zoomFactor;
            StickyImage.Height = _originalHeight * _zoomFactor;

            ShowBadge($"缩放: {(int)(_zoomFactor * 100)}%");
            e.Handled = true;
        }
    }

    private void ShowBadge(string text)
    {
        ZoomBadgeText.Text = text;
        ZoomBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseButtonClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnMenuCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsImageMode && _bitmapSource != null)
            {
                Clipboard.SetImage(_bitmapSource);
            }
            else if (!string.IsNullOrEmpty(StickyTextBox.Text))
            {
                Clipboard.SetText(StickyTextBox.Text);
            }
        }
        catch
        {
            // 忽略剪贴板暂态占用
        }
    }

    private void OnMenuSaveAsClicked(object sender, RoutedEventArgs e)
    {
        if (!IsImageMode || _bitmapSource == null)
        {
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
            DefaultExt = ".png",
            FileName = $"QuickClip_Sticky_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                BitmapEncoder encoder = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? new JpegBitmapEncoder()
                    : new PngBitmapEncoder();

                encoder.Frames.Add(BitmapFrame.Create(_bitmapSource));
                using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存图片失败: {ex.Message}", "贴图提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnMenuResetZoomClicked(object sender, RoutedEventArgs e)
    {
        if (!IsImageMode) return;
        _zoomFactor = 1.0;
        StickyImage.Width = _originalWidth;
        StickyImage.Height = _originalHeight;
        ShowBadge("缩放: 100%");
    }

    private void OnMenuOpacity100Clicked(object sender, RoutedEventArgs e)
    {
        Opacity = 1.0;
        ShowBadge("不透明度: 100%");
    }

    private void OnMenuOpacity75Clicked(object sender, RoutedEventArgs e)
    {
        Opacity = 0.75;
        ShowBadge("不透明度: 75%");
    }

    private void OnMenuOpacity50Clicked(object sender, RoutedEventArgs e)
    {
        Opacity = 0.5;
        ShowBadge("不透明度: 50%");
    }

    private void OnMenuCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
