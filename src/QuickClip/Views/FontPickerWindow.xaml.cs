using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using Microsoft.Win32;
using QuickClip.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace QuickClip.Views;

/// <summary>
/// 轻量系统字体选择弹窗：列出本机安装字体名称，支持拼音搜索与外部字体文件选择。
/// 
/// @author xudong.hua,gemini
/// @since 2026-09-21 20:30 星期一
/// </summary>
public partial class FontPickerWindow : Window
{
    private readonly List<string> _allFonts = new();

    /// <summary>用户最终选定的字体标识（系统字体名或外部字体绝对路径）。</summary>
    public string? SelectedFont { get; private set; }

    public FontPickerWindow(string? currentFont)
    {
        InitializeComponent();
        LoadSystemFonts(currentFont);
        KeyDown += OnWindowKeyDown;
    }

    private void LoadSystemFonts(string? currentFont)
    {
        var names = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f =>
            {
                if (f.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("zh-cn"), out string? zh) &&
                    !string.IsNullOrWhiteSpace(zh))
                {
                    return zh;
                }

                if (f.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string? en) &&
                    !string.IsNullOrWhiteSpace(en))
                {
                    return en;
                }

                return f.Source;
            })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _allFonts.Clear();
        _allFonts.AddRange(names);

        FontListBox.ItemsSource = _allFonts;

        string current = AppFontService.ToDisplayName(currentFont);
        if (_allFonts.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            FontListBox.SelectedItem = current;
            FontListBox.ScrollIntoView(current);
        }
        else if (FontListBox.Items.Count > 0)
        {
            FontListBox.SelectedIndex = 0;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(FontListBox.ItemsSource) is not ICollectionView view)
        {
            return;
        }

        string q = (SearchBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = obj => obj is string name &&
                (name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 PinyinUtil.GetInitials(name).Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        view.Refresh();
        if (FontListBox.Items.Count > 0 && FontListBox.SelectedItem == null)
        {
            FontListBox.SelectedIndex = 0;
        }
    }

    private void OnFontListBoxDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FontListBox.SelectedItem != null)
        {
            ConfirmSelection();
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        if (FontListBox.SelectedItem is string selectedName)
        {
            SelectedFont = selectedName;
            DialogResult = true;
            Close();
        }
    }

    private void OnBrowseFileClicked(object sender, RoutedEventArgs e)
    {
        string userFontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");

        string initialDir = Directory.Exists(userFontsDir)
            ? userFontsDir
            : Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        var dialog = new OpenFileDialog
        {
            Title = "选择字体文件",
            Filter = "所有文件 (*.*)|*.*|字体文件 (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",
            FilterIndex = 1,
            InitialDirectory = initialDir,
            DereferenceLinks = true,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) == true && File.Exists(dialog.FileName))
        {
            SelectedFont = dialog.FileName;
            DialogResult = true;
            Close();
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
