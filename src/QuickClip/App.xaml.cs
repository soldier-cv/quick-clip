using System.Windows.Interop;
using System.Windows.Media;
using QuickClip.Services;
using QuickClip.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QuickClip;

/// <summary>QuickClip 应用入口，负责单实例、主题与服务装配。</summary>
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private AppServices? _services;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // 全局异常兜底：记录日志，UI 线程异常不直接崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 单实例保护：旧实例退出未完成时短暂重试，避免误报「已在运行」
        _mutex = new Mutex(false, @"Local\QuickClip_SingleInstance");
        bool acquired = false;
        for (int attempt = 0; attempt < 10 && !acquired; attempt++)
        {
            acquired = _mutex.WaitOne(0);
            if (!acquired)
            {
                Thread.Sleep(200);
            }
        }

        if (!acquired)
        {
            // 二次启动唤起：向已有实例广播唤起消息，直接呼出面板
            uint msg = QuickClip.Native.NativeMethods.RegisterWindowMessage("QUICKCLIP_SHOW_WINDOW_MSG");
            QuickClip.Native.NativeMethods.PostMessage((IntPtr)QuickClip.Native.NativeMethods.HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 渲染环境检测：远程 / 虚拟显示驱动下 WPF 硬件渲染可能黑屏，自动降级
        bool remoteRender = RenderEnvironment.IsRemoteOrVirtualDisplay();
        if (remoteRender)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        _services = new AppServices();
        _services.Initialize();

        // 按用户设置应用主题（写入 DynamicResource + 关闭 DWM 材质）
        ThemeService.Apply(_services.Settings.Theme);

        var viewModel = new MainViewModel(_services);
        var window = new MainWindow(viewModel, _services) { DataContext = viewModel };
        _services.MainWindow = window;

        // 首次启动展示主窗口，便于用户了解工具已就绪
        window.Show();
        window.Activate();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _services?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>UI 线程异常：记录日志并标记已处理，避免程序直接崩溃。</summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        DebugLog.LogException("UI 线程未处理异常", e.Exception);
        e.Handled = true;
    }

    /// <summary>进程级致命异常：记录完整堆栈后交由系统退出。</summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            DebugLog.LogException("进程级未处理异常（即将退出）", ex);
        }
    }

    /// <summary>未观察的任务异常：记录后标记已观察，防止进程被终结。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DebugLog.LogException("未观察任务异常", e.Exception);
        e.SetObserved();
    }
}
