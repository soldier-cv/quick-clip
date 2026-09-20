using System.Windows;
using Application = System.Windows.Application;
using QuickClip.Models;
using QuickClip.Views;
using Wpf.Ui.Controls;

namespace QuickClip.Services;

/// <summary>
/// 应用内轻量级 Toast 提示服务：
/// 管理浮动提示窗口的展示、内容复用、淡入淡出及右下角对齐。
/// 替代原先沉重且停留时间过长的 Windows 系统托盘气泡通知。
/// </summary>
public sealed class ToastService
{
    private ToastWindow? _currentToast;
    private readonly object _lock = new();
    private readonly SettingsService? _settings;
    private readonly Func<ToastSize>? _getToastSize;

    public ToastService(SettingsService? settings = null)
    {
        _settings = settings;
    }

    public ToastService(Func<ToastSize> getToastSize)
    {
        _getToastSize = getToastSize;
    }

    public ToastSize CurrentSize => _getToastSize?.Invoke() ?? _settings?.ToastSize ?? ToastSize.Medium;

    /// <summary>
    /// 展示一条轻量级 Toast 提示。
    /// </summary>
    /// <param name="title">提示标题或单行主要内容。</param>
    /// <param name="message">可选的补充描述。</param>
    /// <param name="symbol">图标，默认为 Info24。</param>
    /// <param name="durationSeconds">停留秒数，默认 1.8 秒。</param>
    /// <param name="size">指定尺寸，未指定时采用当前设置。</param>
    public void Show(string title, string? message = null, SymbolRegular symbol = SymbolRegular.Info24, double durationSeconds = 1.8, ToastSize? size = null)
    {
        var actualSize = size ?? CurrentSize;
        RunOnUi(() =>
        {
            lock (_lock)
            {
                if (_currentToast != null)
                {
                    try
                    {
                        _currentToast.UpdateContent(title, message, symbol, durationSeconds, actualSize);
                        return;
                    }
                    catch
                    {
                        _currentToast = null;
                    }
                }

                var toast = new ToastWindow();
                _currentToast = toast;

                toast.Closed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_currentToast == toast)
                        {
                            _currentToast = null;
                        }
                    }
                };

                toast.Show();
                toast.UpdateContent(title, message, symbol, durationSeconds, actualSize);
            }
        });
    }

    /// <summary>
    /// 立即隐藏当前正在显示的 Toast。
    /// </summary>
    public void Dismiss()
    {
        RunOnUi(() =>
        {
            lock (_lock)
            {
                _currentToast?.StartFadeOut();
            }
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
