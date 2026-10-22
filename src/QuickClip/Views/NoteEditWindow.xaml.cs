using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QuickClip.Views;

/// <summary>编辑单条剪贴历史的备注。空白保存视为清除。</summary>
public partial class NoteEditWindow : FluentWindow
{
    /// <summary>用户确认后的备注。空白为 null，表示清除。</summary>
    public string? ResultNote { get; private set; }

    public NoteEditWindow(string? note, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        NoteBox.Text = note ?? string.Empty;

        Loaded += (_, _) =>
        {
            NoteBox.Focus();
            NoteBox.CaretIndex = NoteBox.Text.Length;
        };
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e) => Save();

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        ResultNote = null;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Save()
    {
        ResultNote = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        DialogResult = true;
    }
}
