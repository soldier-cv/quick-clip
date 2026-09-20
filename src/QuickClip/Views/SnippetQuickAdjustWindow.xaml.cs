using System.Windows;
using System.Windows.Input;
using QuickClip.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Wpf.Ui.Controls;

namespace QuickClip.Views;

/// <summary>
/// 短语微调填入窗口：
/// 允许用户在保留占位符的短语模板基础上临时增补或修改内容（不破坏原始模板），
/// 并提供一键「应用并粘贴」或「仅复制」功能。
/// 
/// @author xudong.hua,gemini
/// @since 2026-09-20 17:00 星期日
/// </summary>
public partial class SnippetQuickAdjustWindow : FluentWindow
{
    private SnippetItem? _currentSnippet;

    public event Action<string, SnippetItem?>? ApplyAndPasteRequested;
    public event Action<string, SnippetItem?>? CopyOnlyRequested;

    public SnippetItem? CurrentSnippet => _currentSnippet;

    public SnippetQuickAdjustWindow(SnippetItem snippet, Window? owner = null)
    {
        InitializeComponent();

        if (owner != null)
        {
            Owner = owner;
        }

        UpdateSnippet(snippet);

        Loaded += (_, _) =>
        {
            ContentBox.Focus();
            ContentBox.CaretIndex = ContentBox.Text.Length;
        };
    }

    /// <summary>
    /// 当用户在主面板点击另一条短语微调时，无需重开窗口，直接切换当前展示的内容。
    /// </summary>
    public void UpdateSnippet(SnippetItem snippet)
    {
        _currentSnippet = snippet;
        string title = snippet?.Title ?? string.Empty;
        string displayTitle = string.IsNullOrWhiteSpace(title) ? "微调填入" : $"微调填入 - {title}";
        Title = displayTitle;
        WindowTitleBar.Title = displayTitle;
        ContentBox.Text = snippet?.Content ?? string.Empty;
        ContentBox.Focus();
        ContentBox.CaretIndex = ContentBox.Text.Length;
    }

    private void OnApplyAndPasteClicked(object sender, RoutedEventArgs e)
    {
        ApplyAndPaste();
    }

    private void OnCopyOnlyClicked(object sender, RoutedEventArgs e)
    {
        string text = ContentBox.Text;
        CopyOnlyRequested?.Invoke(text, _currentSnippet);
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyAndPaste()
    {
        string text = ContentBox.Text;
        ApplyAndPasteRequested?.Invoke(text, _currentSnippet);
        Close();
    }

    private void OnContentBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Ctrl+Enter 快速应用并粘贴
            ApplyAndPaste();
            e.Handled = true;
        }
        // 普通 Enter 及 Shift+Enter 均不拦截，保留 TextBox 自然换行行为
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
