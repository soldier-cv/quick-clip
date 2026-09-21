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
        DebugLog.Log($"开始启动 QuickClip: pid={Environment.ProcessId}, autostart={fromAutostart}, args=[{string.Join(", ", e.Args)}]");

        // 卸载/维护入口：按接管前快照还原系统剪贴板状态后立即退出（不参与单实例判定）
        if (HasRestoreClipboardArg(e.Args))
        {
            RestoreSystemClipboardAndExit();
            return;
        }

        _mutex = new Mutex(false, @"Local\QuickClip_SingleInstance");
        bool acquired = TryAcquireMutex(_mutex, retries: 10, delayMs: 200);
        DebugLog.Log($"单实例互斥锁获取结果: acquired={acquired}");

        if (!acquired && UpdateService.IsInstalledCopy() && TryReplaceForeignInstances())
        {
            acquired = TryAcquireMutex(_mutex, retries: 15, delayMs: 300);
        }

        if (!acquired)
        {
            DebugLog.Log("检测到已有 QuickClip 实例，发送显示消息后退出");
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

        // 按用户设置应用主题（写入 DynamicResource + 关闭 DWM 材质）
        // 服务装配与窗口创建全程兜底：失败必须退出进程，
        // 否则会留下「没有窗口也没有托盘、却占着单实例互斥锁」的僵尸进程。
        try
        {
            _services = new AppServices();
            ThemeService.Apply(_services.Settings.Theme);
            AppFontService.Apply(_services.Settings.UiFontFamily);

            var viewModel = new MainViewModel(_services);
            var window = new MainWindow(viewModel, _services) { DataContext = viewModel };
            _services.MainWindow = window;
            _services.StackPaste.AttachPanel(window);

            // 关键：先创建窗口句柄（触发 SourceInitialized → Monitor.Attach 挂剪贴板监听），
            // 再启动热键。开机自启动分支不会 Show()，若不主动建句柄，剪贴板监听永远挂不上，
            // 表现为「重启后按 Win+V 面板能开，但历史里一条都没有」。
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            DebugLog.Log($"主窗口句柄已创建: {handle}");

            if (!_services.Initialize(fromAutostart))
            {
                DebugLog.Log("服务初始化要求当前进程退出");
                Shutdown();
                return;
            }

            // 首次手动启动展示主窗口，便于用户了解工具已就绪；开机自启动则静默驻留托盘待命
            if (!fromAutostart)
            {
                window.Show();
                window.Activate();
            }
            else
            {
                DebugLog.Log("开机自启动：保持后台静默运行，安排空闲静默预热");
                // 延迟 1.5 秒（避开系统开机启动高峰、等待数据库异步加载与托盘就绪），在 UI 线程空闲时静默预热
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    var dispatcher = window.Dispatcher;
                    if (dispatcher is { HasShutdownStarted: false })
                    {
                        dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                            new Action(() => window.WarmUp()));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("启动失败，即将退出", ex);
            try
            {
                _services?.Dispose();
            }
            catch (Exception disposeEx)
            {
                DebugLog.LogException("启动失败后清理服务异常", disposeEx);
            }

            _services = null;
            System.Windows.MessageBox.Show(
                "QuickClip 启动失败，已退出。\n\n" + ex.Message +
                "\n\n详细信息见 %LOCALAPPDATA%\\QuickClip\\debug.log",
                "QuickClip",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown();
            return;
        }

        DebugLog.Log("QuickClip 启动完成");
    }

    /// <summary>卸载/维护模式：还原接管前的系统剪贴板状态后退出（不加载任何服务）。</summary>
    private void RestoreSystemClipboardAndExit()
    {
        try
        {
            var paths = new AppPaths();
            bool ok = SystemClipboardService.RestoreSystemClipboard(paths);
            DebugLog.Log($"--restore-clipboard 完成: ok={ok}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("--restore-clipboard 失败", ex);
        }

        Shutdown();
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

    private static bool HasRestoreClipboardArg(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, RestoreClipboardArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>卸载程序调用的维护参数：还原接管前的系统剪贴板状态。</summary>
    public const string RestoreClipboardArgument = "--restore-clipboard";

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
