using System.IO;
using System.Windows.Threading;

namespace QuickClip.Services;

/// <summary>应用服务装配与生命周期管理。</summary>
public sealed class AppServices : IDisposable
{
    private readonly System.Threading.Timer? _cleanupTimer;
    private DateTime _lastVacuumDate = DateTime.MinValue;

    public AppPaths Paths { get; }
    public SettingsService Settings { get; }
    public HotkeyService Hotkey { get; }
    public DatabaseService Database { get; private set; }
    public QrCodeService Qr { get; }
    public OcrService Ocr { get; }
    public PasteService Paste { get; }
    public ClipboardMonitor Monitor { get; }
    public ClipboardPipeline Pipeline { get; }
    public TrayIconService Tray { get; }
    public UpdateService Update { get; }

    /// <summary>主窗口引用（由 App 在创建后注入）。</summary>
    public MainWindow? MainWindow { get; set; }

    public AppServices()
    {
        Paths = new AppPaths();
        Paths.EnsureCreated();

        Settings = new SettingsService(Paths.SettingsPath);
        Settings.Load();

        Database = new DatabaseService(Settings.DatabasePath ?? Paths.DatabasePath);
        Qr = new QrCodeService();
        Ocr = new OcrService(Settings);
        Paste = new PasteService();
        Pipeline = new ClipboardPipeline(Paths, Database, Qr, Paste, Settings);
        Hotkey = new HotkeyService();
        Monitor = new ClipboardMonitor();
        Tray = new TrayIconService();
        Update = new UpdateService();

        // 冷启动优先热键/监听；清理延后到 8 分钟，避免与首屏争抢
        _cleanupTimer = new System.Threading.Timer(CleanupTick, null, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(60));
    }

    private bool _appliedAutoStart;

    /// <summary>
    /// 装配运行时服务。返回 false 表示已拉起静默安装，调用方应立即退出。
    /// fromAutostart 为 true 时不自动弹 UAC，只提示已下载的更新。
    /// </summary>
    public bool Initialize(bool fromAutostart = false)
    {
        AutoStartService.MigrateInstalledAutostart();

        bool registryAutoStart = AutoStartService.IsEnabled();
        if (registryAutoStart != Settings.AutoStart)
        {
            Settings.SetAutoStart(registryAutoStart);
        }

        _appliedAutoStart = Settings.AutoStart;

        Update.Attach(Paths, Settings);
        if (!fromAutostart
            && Update.ShouldAutoApplyOnStartup()
            && Update.TryApplyPending(out string applyMessage, out bool shouldExit)
            && shouldExit)
        {
            DebugLog.Log($"启动时自动安装已下载更新: {applyMessage}");
            return false;
        }

        var ui = Dispatcher.CurrentDispatcher;
        Update.PendingChanged += pending =>
            ui.BeginInvoke(() => Tray.SetInstallUpdateVisible(pending != null, pending?.TagName));
        Update.UserNotify += (title, message) =>
            ui.BeginInvoke(() => Tray.ShowBalloonTip(title, message));
        if (Update.Pending != null)
        {
            Tray.SetInstallUpdateVisible(true, Update.Pending.TagName);
            if (fromAutostart)
            {
                Tray.ShowBalloonTip("QuickClip", UpdateService.ReadyNotifyText(Update.Pending.TagName));
            }
        }

        Update.StartSilentChecks();

        // 先挂监听 + 热键（核心路径），再同步托盘
        Monitor.ClipboardUpdated += Pipeline.OnClipboardUpdated;
        // 历史项复制/粘贴回写系统剪贴板时抑制捕获，避免列表顶部再插一条相同记录
        Paste.SelfClipboardWrite += () => Pipeline.SuppressCapture();
        Hotkey.HotkeyInstallFailed += message => Tray.ShowBalloonTip("QuickClip", message);
        Settings.Changed += OnSettingsChanged;
        Hotkey.Start(Dispatcher.CurrentDispatcher, Settings);

        Tray.SetAutoStartChecked(Settings.AutoStart);
        return true;
    }

    /// <summary>设置变更联动：热键重新注册；自启动仅在状态变化时写注册表并同步托盘勾选。</summary>
    private void OnSettingsChanged()
    {
        Hotkey.ApplyHotkeys(Settings);

        if (_appliedAutoStart != Settings.AutoStart)
        {
            bool desired = Settings.AutoStart;
            bool ok = desired ? AutoStartService.Enable() : AutoStartService.Disable();
            if (ok)
            {
                _appliedAutoStart = desired;
                Tray.SetAutoStartChecked(desired);
            }
            else
            {
                // 注册表写入失败：回滚设置，避免 UI 显示已开启但实际未生效
                DebugLog.Log($"开机自启动切换失败，回滚为 {_appliedAutoStart}");
                Settings.SetAutoStart(_appliedAutoStart);
                Tray.SetAutoStartChecked(_appliedAutoStart);
                Tray.ShowBalloonTip("QuickClip", desired
                    ? "启用开机自启动失败，请检查权限后重试"
                    : "关闭开机自启动失败，请检查权限后重试");
            }
        }
    }

    private async void CleanupTick(object? state)
    {
        try
        {
            var trimmed = await Database.TrimToMaxItemsAsync(Settings.MaxHistoryItems);
            foreach (var (_, preview) in trimmed)
            {
                ThumbnailCache.RemoveByPath(preview);
            }

            await Database.CleanupOrphanPreviewsAsync(Paths);

            if (trimmed.Count > 0)
            {
                DebugLog.Log($"历史清理: 超条数({Settings.MaxHistoryItems})删 {trimmed.Count} 条");
            }

            if (_lastVacuumDate.Date != DateTime.Now.Date)
            {
                await Database.VacuumAsync();
                _lastVacuumDate = DateTime.Now;
                DebugLog.Log("已执行数据库 VACUUM 压缩");
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("自动清理失败", ex);
        }
    }

    /// <summary>
    /// 修补历史预览图里「Alpha 全 0」的 PNG，并对尚未识别的图片重跑二维码。
    /// 有改动返回 true，调用方应刷新列表。
    /// </summary>
    public async Task<bool> RepairImageHistoryAsync()
    {
        var items = await Database.GetRecentAsync(Settings.MaxHistoryItems);
        bool changed = false;

        foreach (var item in items)
        {
            if (item.ContentType != Models.ClipboardContentType.Image ||
                string.IsNullOrEmpty(item.PreviewPath) ||
                !File.Exists(item.PreviewPath))
            {
                continue;
            }

            string path = item.PreviewPath;
            bool repaired = await Task.Run(() => ClipboardImageNormalizer.RepairFileIfFullyTransparent(path));
            if (repaired)
            {
                ThumbnailCache.RemoveByPath(path);
                changed = true;
            }

            if (!string.IsNullOrEmpty(item.QrContent))
            {
                continue;
            }

            string? qr = await Task.Run(() => Qr.Decode(path));
            if (string.IsNullOrEmpty(qr))
            {
                continue;
            }

            await Database.UpdateQrContentAsync(item.Id, qr);
            changed = true;
            DebugLog.Log($"历史图片补识别二维码: id={item.Id}");
        }

        return changed;
    }

    /// <summary>清除今日非置顶历史。</summary>
    public async Task<int> ClearTodayHistoryAsync()
    {
        int n = await Database.DeleteTodayUnpinnedAsync();
        await Database.CleanupOrphanPreviewsAsync(Paths);
        return n;
    }

    /// <summary>清空全部非置顶历史（置顶保留），并删除对应预览图。</summary>
    public async Task<int> ClearAllUnpinnedHistoryAsync()
    {
        var (n, previews) = await Database.DeleteAllUnpinnedAsync();
        foreach (string path in previews)
        {
            ThumbnailCache.RemoveByPath(path);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 预览文件占用时可忽略
            }
        }

        await Database.CleanupOrphanPreviewsAsync(Paths);
        return n;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        Settings.Changed -= OnSettingsChanged;
        Monitor.Detach();
        Hotkey.Dispose();
        Tray.Dispose();
        Update.Dispose();
        Database.Dispose();
    }
}
