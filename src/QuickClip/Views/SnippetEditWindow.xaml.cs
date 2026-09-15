using System.Windows;
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
        CategoryCombo.Text = string.IsNullOrWhiteSpace(_target.Category) ? "通用" : _target.Category;
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

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        string category = CategoryCombo.Text.Trim();
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
        _target.Category = string.IsNullOrWhiteSpace(category) ? "通用" : category;
        _target.Content = content;

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
