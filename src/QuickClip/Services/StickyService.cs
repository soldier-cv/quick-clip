using System.Windows;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using System.Windows.Media.Imaging;
using QuickClip.Models;
using QuickClip.Views;

namespace QuickClip.Services;

/// <summary>桌面贴图/便签服务：管理当前在桌面上置顶的贴图与便签窗口。</summary>
public sealed class StickyService
{
    private readonly List<StickyWindow> _activeWindows = new();
    private readonly object _lock = new();

    /// <summary>将指定的剪贴板历史条目钉在桌面上。</summary>
    public bool PinItem(ClipboardItem item)
    {
        if (item == null)
        {
            return false;
        }

        if (item.ContentType == ClipboardContentType.Image && !string.IsNullOrEmpty(item.PreviewPath))
        {
            return PinImagePath(item.PreviewPath);
        }

        string text = item.TextContent ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return PinText(text);
        }

        return false;
    }

    /// <summary>以图片位图直接贴图。</summary>
    public bool PinImage(BitmapSource bitmap)
    {
        if (bitmap == null) return false;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            var win = new StickyWindow();
            win.InitializeImage(bitmap);
            RegisterWindow(win);
            win.Show();
        });
        return true;
    }

    /// <summary>以本地图片文件路径贴图。</summary>
    public bool PinImagePath(string filePath)
    {
        bool success = false;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var win = new StickyWindow();
            if (win.InitializeImagePath(filePath))
            {
                RegisterWindow(win);
                win.Show();
                success = true;
            }
            else
            {
                win.Close();
            }
        });
        return success;
    }

    /// <summary>以纯文本方式创建置顶便签。</summary>
    public bool PinText(string text)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var win = new StickyWindow();
            win.InitializeText(text);
            RegisterWindow(win);
            win.Show();
        });
        return true;
    }

    /// <summary>尝试将当前系统剪贴板的内容直接贴图到桌面。</summary>
    public bool PinFromCurrentClipboard()
    {
        bool success = false;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        PinImage(img);
                        success = true;
                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        PinText(text);
                        success = true;
                        return;
                    }
                }
            }
            catch
            {
                // 忽略剪贴板暂态占用
            }
        });
        return success;
    }

    private void RegisterWindow(StickyWindow win)
    {
        lock (_lock)
        {
            _activeWindows.Add(win);
        }

        win.Closed += (s, e) =>
        {
            lock (_lock)
            {
                _activeWindows.Remove(win);
            }
        };
    }

    /// <summary>关闭当前全部已打开的贴图窗口。</summary>
    public void CloseAll()
    {
        List<StickyWindow> copy;
        lock (_lock)
        {
            copy = new List<StickyWindow>(_activeWindows);
            _activeWindows.Clear();
        }

        foreach (var win in copy)
        {
            try
            {
                win.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
