using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using QuickClip.Native;
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
        SessionEnding += OnSessionEnding;

        bool fromAutostart = HasAutostartArg(e.Args);

        _mutex = new Mutex(false, @"Local\QuickClip_SingleInstance");
        bool acquired = TryAcquireMutex(_mutex, retries: 10, delayMs: 200);

        if (!acquired && UpdateService.IsInstalledCopy() && TryReplaceForeignInstances())
        {
            acquired = TryAcquireMutex(_mutex, retries: 15, delayMs: 300);
        }

        if (!acquired)
        {
            uint msg = NativeMethods.RegisterWindowMessage("QUICKCLIP_SHOW_WINDOW_MSG");
            NativeMethods.PostMessage((IntPtr)NativeMethods.HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
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
        if (!_services.Initialize(fromAutostart))
        {
            Shutdown();
            return;
        }

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

    private void OnSessionEnding(object sender, System.Windows.SessionEndingCancelEventArgs e)
    {
        DebugLog.Log($"系统会话结束 ({e.ReasonSessionEnding})，退出进程");
        _services?.MainWindow?.PrepareForSystemExit();
        Shutdown();
    }

    private static bool HasAutostartArg(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, AutoStartService.AutostartArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAcquireMutex(Mutex mutex, int retries, int delayMs)
    {
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                if (mutex.WaitOne(0))
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                return true;
            }

            Thread.Sleep(delayMs);
        }

        return false;
    }

    /// <summary>安装版：结束占用互斥锁且路径不同的旧进程。读不到路径或同路径则不杀。</summary>
    private static bool TryReplaceForeignInstances()
    {
        string? myPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(myPath))
        {
            return false;
        }

        bool killedForeign = false;
        bool sawSamePath = false;
        foreach (Process process in Process.GetProcessesByName("QuickClip"))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                string? other = TryGetProcessPath(process);
                if (string.IsNullOrEmpty(other))
                {
                    continue;
                }

                if (string.Equals(other, myPath, StringComparison.OrdinalIgnoreCase))
                {
                    sawSamePath = true;
                    continue;
                }

                DebugLog.Log($"结束路径不同的旧实例: pid={process.Id} path={other}");
                process.Kill();
                process.WaitForExit(3000);
                killedForeign = true;
            }
            catch (Exception ex)
            {
                DebugLog.LogException("结束旧实例失败", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        return killedForeign && !sawSamePath;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
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
