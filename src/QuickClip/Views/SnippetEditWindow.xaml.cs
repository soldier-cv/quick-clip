using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using QuickClip.Models;
using Wpf.Ui.Controls;

namespace QuickClip.Views;

/// <summary>常用短语新增与编辑窗口。</summary>
public partial class SnippetEditWindow : FluentWindow
{
    private readonly SnippetItem _target;
    private readonly bool _isEdit;

    public event Action<SnippetItem>? Saved;
    public SnippetItem ResultItem => _target;

    public SnippetEditWindow(SnippetItem? existing = null, Window? owner = null)
    {
        InitializeComponent();
        if (owner != null)
        {
            Owner = owner;
        }

        _isEdit = existing != null;
        _target = existing ?? new SnippetItem();

        Title = _isEdit ? "编辑常用短语" : "新增常用短语";
        TitleBox.Text = _target.Title;
        string currentCategory = string.IsNullOrWhiteSpace(_target.Category) ? "通用" : _target.Category;
        SelectOrAddCategory(currentCategory);
        ContentBox.Text = _target.Content;

        Loaded += (s, e) =>
        {
            if (string.IsNullOrEmpty(TitleBox.Text))
            {
                TitleBox.Focus();
            }
            else
            {
                ContentBox.Focus();
            }
        };
    }

    private void SelectOrAddCategory(string cat)
    {
        foreach (var item in CategoryCombo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), cat, StringComparison.OrdinalIgnoreCase))
            {
                CategoryCombo.SelectedItem = cbi;
                return;
            }
        }
        var newItem = new ComboBoxItem { Content = cat };
        CategoryCombo.Items.Add(newItem);
        CategoryCombo.SelectedItem = newItem;
    }

    private string GetSelectedCategory()
    {
        if (CategoryCombo.SelectedItem is ComboBoxItem cbi && cbi.Content is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }
        return "通用";
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        string category = GetSelectedCategory();
        string content = ContentBox.Text;

        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show("短语标题不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show("短语内容不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            ContentBox.Focus();
            return;
        }

        _target.Title = title;
        _target.Category = category;
        _target.Content = content;

        Saved?.Invoke(_target);
        Close();
    }

    private void OnPlaceholderBadgeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string placeholder })
        {
            int caret = ContentBox.CaretIndex;
            string current = ContentBox.Text ?? string.Empty;
            if (caret < 0 || caret > current.Length)
            {
                caret = current.Length;
            }

            ContentBox.Text = current.Insert(caret, placeholder);
            ContentBox.CaretIndex = caret + placeholder.Length;
            ContentBox.Focus();
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Close();
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
