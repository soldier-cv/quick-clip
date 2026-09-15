using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QuickClip.Views;

/// <summary>收集栈浮动 HUD 窗口：展示当前入栈项数与下个出栈预览。</summary>
public partial class StackHudWindow : Window
{
    public event Action? SplitLinesRequested;
    public event Action? ClearRequested;
    public event Action? ExitRequested;

    public StackHudWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 放置在主工作区右下角
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    public void UpdateState(int count, string? nextItemPreview)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateState(count, nextItemPreview));
            return;
        }

        CountBadge.Text = $"{count} 项";
        if (count == 0)
        {
            NextItemPreview.Text = "当前栈为空，复制内容将自动入栈";
        }
        else
        {
            string preview = nextItemPreview?.Replace("\r", " ").Replace("\n", " ").Trim() ?? string.Empty;
            if (preview.Length > 30)
            {
                preview = preview[..30] + "…";
            }
            NextItemPreview.Text = $"下一个出栈: {preview}";
        }
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
                // 忽略
            }
        }
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
