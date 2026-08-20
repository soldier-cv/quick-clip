using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace QuickClip.Services;

/// <summary>
/// 发布渠道：统一采用依赖运行时的轻量安装包。
/// </summary>
public enum ReleaseChannel
{
    Setup
}

/// <summary>已下载、等待用户安装的更新。</summary>
public sealed class PendingUpdate
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string LocalPath { get; init; }
    public required ReleaseChannel Channel { get; init; }
}

/// <summary>自动下载更新失败的信息。</summary>
public sealed class DownloadFailedInfo
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string DownloadUrl { get; init; }
    public required string ErrorMessage { get; init; }
    public required DateTime FailedTimeUtc { get; init; }
}

/// <summary>更新流程当前阶段（检查与下载拆开，避免界面长时间停在「正在检查」）。</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,
    Ready,
    Failed,
    UpToDate
}

/// <summary>更新流程快照，供设置页/托盘即时刷新。</summary>
public sealed class UpdateActivity
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;
    public string Message { get; init; } = string.Empty;
    public string? TagName { get; init; }
    public long BytesReceived { get; init; }
    public long BytesTotal { get; init; }
}

/// <summary>
/// 版本检查、安装包下载、12小时调度与7次重试。
/// 
/// @author xudong.hua,gemini
/// @since 2026-08-19 16:18 星期三
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepoOwner = "soldier-cv";
    private const string RepoName = "quick-clip";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string LatestReleasePageUrl = $"{ReleasesPageUrl}/latest";
    internal const string InstalledMarkerFileName = "QuickClip.installed";
    private const string AutoApplyStampFileName = "auto-apply.stamp";
    private static readonly TimeSpan AutoApplyDeferral = TimeSpan.FromHours(24);
    private const long MaxDownloadBytes = 400L * 1024 * 1024;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NormalInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(3);
    private const int MaxRetryAttempts = 7;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PageFallbackTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProgressUiInterval = TimeSpan.FromMilliseconds(200);

    private readonly HttpClient _http = CreateHttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppPaths? _paths;
    private SettingsService? _settings;
    private System.Threading.Timer? _scheduleTimer;
    private int _consecutiveFailures;
    private string? _notifiedFoundTag;
    private string? _notifiedFailTag;
    private DateTime _lastProgressUiUtc = DateTime.MinValue;

    /// <summary>当前程序版本（来自程序集版本号）。</summary>
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>安装目录旁有 QuickClip.installed 才视为安装版（绿色版不得改写自启动）。</summary>
    public static bool IsInstalledCopy()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(exe);
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, InstalledMarkerFileName));
    }

    /// <summary>启动时是否应自动安装已下载包（开机自启、24h 内已尝试过则否）。</summary>
    public bool ShouldAutoApplyOnStartup()
    {
        if (Pending == null || !File.Exists(Pending.LocalPath))
        {
            return false;
        }

        return !IsAutoApplyDeferred(Pending.Version);
    }

    /// <summary>当前发布渠道：固定为安装版。</summary>
    public static ReleaseChannel CurrentChannel => ReleaseChannel.Setup;

    public static string ChannelLabel => "安装版";

    /// <summary>已下载更新后的动作文案：启动 Setup 安装程序。</summary>
    public static string ApplyActionLabel => "立即更新";

    /// <summary>下载完成后的托盘/设置提示。</summary>
    public static string ReadyNotifyText(string tagName) =>
        $"发现新版本 {tagName}，已下载。点击即可安装，或下次手动启动自动安装";

    public static string FoundNotifyText(string tagName) =>
        $"发现新版本 {tagName}，正在下载";

    public PendingUpdate? Pending { get; private set; }

    /// <summary>自动下载失败时的标记信息（为 null 表示无失败）。</summary>
    public DownloadFailedInfo? DownloadFailed { get; private set; }

    public UpdateActivity Activity { get; private set; } = new() { Phase = UpdatePhase.Idle };

    /// <summary>待安装更新变化（可能来自后台线程，订阅方需切回 UI）。</summary>
    public event Action<PendingUpdate?>? PendingChanged;

    /// <summary>下载失败状态变化。</summary>
    public event Action<DownloadFailedInfo?>? DownloadFailedChanged;

    /// <summary>检查/下载阶段变化（可能来自后台线程）。</summary>
    public event Action<UpdateActivity>? ActivityChanged;

    /// <summary>需要提示用户时触发（title, message）。</summary>
    public event Action<string, string>? UserNotify;

    public void Attach(AppPaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
        paths.EnsureCreated();
        TryRestorePending();
    }

    /// <summary>启动后台自动更新调度任务（启动 8 秒后首次执行，通过后每 12 小时一次，失败最多重试 7 次）。</summary>
    public void StartSilentChecks()
    {
        _scheduleTimer?.Dispose();
        _consecutiveFailures = 0;
        _scheduleTimer = new System.Threading.Timer(
            _ => _ = ExecuteScheduledCheckAsync(),
            null,
            InitialDelay,
            Timeout.InfiniteTimeSpan);
        DebugLog.Log($"已启动自动更新调度器：将在 {InitialDelay.TotalSeconds} 秒后执行首次检查");
    }

    /// <summary>
    /// 执行定时检查任务：成功则 12 小时后再次检查；失败重试，7 次失败后暂停重试等待下一次 12 小时常规任务。
    /// </summary>
    private async Task ExecuteScheduledCheckAsync()
    {
        var settings = _settings;
        if (settings == null || !settings.AutoCheckUpdates)
        {
            _scheduleTimer?.Change(NormalInterval, Timeout.InfiniteTimeSpan);
            return;
        }

        DebugLog.Log($"开始执行自动更新检查 (连续失败重试计数: {_consecutiveFailures}/{MaxRetryAttempts})");
        var result = await CheckAndDownloadAsync(interactive: false);

        if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.Ready)
        {
            _consecutiveFailures = 0;
            settings.SetLastUpdateCheckUtc(DateTime.UtcNow);
            _scheduleTimer?.Change(NormalInterval, Timeout.InfiniteTimeSpan);
            DebugLog.Log($"自动更新检查通过（状态: {result.Status}），安排下一次检查在 {NormalInterval.TotalHours} 小时后");

            if (result.Status == UpdateCheckStatus.Ready && result.Pending != null)
            {
                UserNotify?.Invoke("QuickClip", ReadyNotifyText(result.Pending.TagName));
            }
            return;
        }

        // 失败分支（接口失败或下载失败）
        _consecutiveFailures++;
        if (_consecutiveFailures <= MaxRetryAttempts)
        {
            DebugLog.Log($"自动检查更新失败 ({_consecutiveFailures}/{MaxRetryAttempts})：{result.Message}，将在 {RetryInterval.TotalMinutes} 分钟后重试");
            _scheduleTimer?.Change(RetryInterval, Timeout.InfiniteTimeSpan);
        }
        else
        {
            DebugLog.Log($"自动检查更新已连续失败 {MaxRetryAttempts} 次，暂停重试，等待下个 12 小时常规调度周期");
            _consecutiveFailures = 0;
            settings.SetLastUpdateCheckUtc(DateTime.UtcNow);
            _scheduleTimer?.Change(NormalInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 检查 GitHub Releases 是否有更新版本。
    /// 先走 api.github.com；403/超时后再用 Releases/latest 页面回退（国内 API 常被拒）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var api = await TryCheckViaApiAsync();
        if (api.Status != UpdateCheckStatus.Failed)
        {
            return api;
        }

        DebugLog.Log($"GitHub API 不可用，回退 Releases 页面: {api.Message}");
        var page = await TryCheckViaLatestPageAsync();
        if (page.Status != UpdateCheckStatus.Failed)
        {
            return page;
        }

        return UpdateCheckResult.Fail(
            $"检查更新失败：{api.Message}。页面回退也失败。可手动访问 {ReleasesPageUrl}");
    }

    private async Task<UpdateCheckResult> TryCheckViaApiAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            ApplyGitHubHeaders(request, api: true);

            using var cts = new CancellationTokenSource(ApiTimeout);
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string body = await ReadBodySnippetAsync(response);
                string limit = response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem)
                    ? $" remaining={string.Join(',', rem)}"
                    : string.Empty;
                DebugLog.Log($"检查更新 API 失败: HTTP {code}{limit} {body}");
                string detail = code == 404
                    ? "仓库尚无 Releases 或地址不可用"
                    : code == 403
                        ? "HTTP 403（接口被拒或额度用尽）"
                        : $"HTTP {code}";
                return UpdateCheckResult.Fail(detail);
            }

            var dto = await response.Content.ReadFromJsonAsync<GitHubReleaseDto>();
            if (dto == null || string.IsNullOrEmpty(dto.TagName))
            {
                return UpdateCheckResult.Fail("无法解析 GitHub API 响应");
            }

            return FinishCheck(BuildReleaseInfo(dto), "API");
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or OperationCanceledException)
        {
            DebugLog.LogException("检查更新 API 超时", ex);
            return UpdateCheckResult.Fail("连接 GitHub API 超时");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("检查更新 API 异常", ex);
            return UpdateCheckResult.Fail(ex.Message);
        }
    }

    /// <summary>不经过 api.github.com：跟随 /releases/latest 跳转到 /releases/tag/vX.Y.Z。</summary>
    private async Task<UpdateCheckResult> TryCheckViaLatestPageAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleasePageUrl);
            ApplyGitHubHeaders(request, api: false);

            using var cts = new CancellationTokenSource(PageFallbackTimeout);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                DebugLog.Log($"检查更新页面失败: HTTP {(int)response.StatusCode}");
                return UpdateCheckResult.Fail($"HTTP {(int)response.StatusCode}");
            }

            string? final = response.RequestMessage?.RequestUri?.AbsolutePath
                            ?? response.Headers.Location?.OriginalString;
            if (!TryParseTagFromLatestUrl(final, out string tag, out string version))
            {
                return UpdateCheckResult.Fail("无法从 Releases 页面解析版本号");
            }

            var release = new ReleaseInfo
            {
                Version = version,
                TagName = tag,
                DownloadUrl = BuildDirectDownloadUrl(tag),
                AssetSize = 0
            };
            return FinishCheck(release, "页面");
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or OperationCanceledException)
        {
            DebugLog.LogException("检查更新页面超时", ex);
            return UpdateCheckResult.Fail("连接 GitHub 页面超时");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("检查更新页面异常", ex);
            return UpdateCheckResult.Fail(ex.Message);
        }
    }

    private UpdateCheckResult FinishCheck(ReleaseInfo release, string source)
    {
        DebugLog.Log(
            $"检查更新完成({source}): 最新 {release.TagName}，当前 v{CurrentVersion}，渠道 {CurrentChannel}");

        if (!IsNewer(release.Version, CurrentVersion))
        {
            return UpdateCheckResult.UpToDate();
        }

        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            return UpdateCheckResult.Fail(
                $"发现新版本 {release.TagName}，但该版本没有对应「{ChannelLabel}」安装包。可手动访问 {ReleasesPageUrl}");
        }

        return UpdateCheckResult.UpdateAvailable(release);
    }

    /// <summary>检查并按当前渠道下载。查到新版本会立刻推状态，再后台拉安装包。</summary>
    public async Task<UpdateCheckResult> CheckAndDownloadAsync(bool interactive)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero))
        {
            return new UpdateCheckResult
            {
                Status = Activity.Phase == UpdatePhase.Downloading
                    ? UpdateCheckStatus.UpdateAvailable
                    : UpdateCheckStatus.Failed,
                Message = string.IsNullOrEmpty(Activity.Message) ? "正在处理更新…" : Activity.Message
            };
        }

        try
        {
            if (_paths != null &&
                Pending is { } existing &&
                File.Exists(existing.LocalPath) &&
                IsNewer(existing.Version, CurrentVersion))
            {
                SetDownloadFailed(null);
                PublishActivity(new UpdateActivity
                {
                    Phase = UpdatePhase.Ready,
                    Message = $"新版本 {existing.TagName} 已下载",
                    TagName = existing.TagName
                });
                return UpdateCheckResult.Ready(existing);
            }

            PublishActivity(new UpdateActivity
            {
                Phase = UpdatePhase.Checking,
                Message = "正在检查更新…"
            });

            var check = await CheckForUpdateAsync();
            if (check.Status != UpdateCheckStatus.UpdateAvailable || check.Release == null)
            {
                if (check.Status == UpdateCheckStatus.UpToDate)
                {
                    PublishActivity(new UpdateActivity
                    {
                        Phase = UpdatePhase.UpToDate,
                        Message = check.Message ?? $"当前已是最新版本 v{CurrentVersion}"
                    });
                }
                else if (check.Status == UpdateCheckStatus.Failed)
                {
                    PublishActivity(new UpdateActivity
                    {
                        Phase = UpdatePhase.Failed,
                        Message = check.Message ?? "检查更新失败"
                    });
                    if (interactive)
                    {
                        UserNotify?.Invoke("QuickClip", check.Message ?? "检查更新失败");
                    }
                }

                return check;
            }

            if (_paths == null)
            {
                return UpdateCheckResult.Fail("内部错误：更新目录未初始化");
            }

            SetDownloadFailed(null);
            PublishActivity(new UpdateActivity
            {
                Phase = UpdatePhase.Downloading,
                Message = FoundNotifyText(check.Release.TagName),
                TagName = check.Release.TagName
            });
            DebugLog.Log($"发现新版本 {check.Release.TagName}，开始下载 {check.Release.DownloadUrl}");
            NotifyFoundOnce(check.Release.TagName);

            string? local = await DownloadReleaseAsync(check.Release, _paths);
            if (string.IsNullOrEmpty(local))
            {
                var failedInfo = new DownloadFailedInfo
                {
                    Version = check.Release.Version,
                    TagName = check.Release.TagName,
                    DownloadUrl = check.Release.DownloadUrl ?? BuildDirectDownloadUrl(check.Release.TagName),
                    ErrorMessage = $"发现新版本 {check.Release.TagName}，但自动下载失败（网络异常或超时）",
                    FailedTimeUtc = DateTime.UtcNow
                };
                SetDownloadFailed(failedInfo);
                PublishActivity(new UpdateActivity
                {
                    Phase = UpdatePhase.Failed,
                    Message = failedInfo.ErrorMessage,
                    TagName = check.Release.TagName
                });

                var fail = UpdateCheckResult.Fail(failedInfo.ErrorMessage);
                NotifyFailOnce(check.Release.TagName, fail.Message ?? "下载失败");
                return fail;
            }

            SetDownloadFailed(null);
            SetPending(new PendingUpdate
            {
                Version = check.Release.Version,
                TagName = check.Release.TagName,
                LocalPath = local,
                Channel = CurrentChannel
            });
            PublishActivity(new UpdateActivity
            {
                Phase = UpdatePhase.Ready,
                Message = $"新版本 {check.Release.TagName} 已下载",
                TagName = check.Release.TagName
            });

            return UpdateCheckResult.Ready(Pending!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void NotifyFoundOnce(string tagName)
    {
        if (string.Equals(_notifiedFoundTag, tagName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _notifiedFoundTag = tagName;
        UserNotify?.Invoke("QuickClip", FoundNotifyText(tagName));
    }

    private void NotifyFailOnce(string tagName, string message)
    {
        if (string.Equals(_notifiedFailTag, tagName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _notifiedFailTag = tagName;
        UserNotify?.Invoke("QuickClip", message);
    }

    private void PublishActivity(UpdateActivity activity)
    {
        Activity = activity;
        ActivityChanged?.Invoke(activity);
    }

    private void PublishDownloadProgress(string tagName, long received, long total)
    {
        var now = DateTime.UtcNow;
        if (now - _lastProgressUiUtc < ProgressUiInterval && received != total)
        {
            return;
        }

        _lastProgressUiUtc = now;
        PublishActivity(new UpdateActivity
        {
            Phase = UpdatePhase.Downloading,
            Message = FormatDownloadMessage(tagName, received, total),
            TagName = tagName,
            BytesReceived = received,
            BytesTotal = total
        });
    }

    internal static string FormatDownloadMessage(string tagName, long received, long total)
    {
        if (total > 0)
        {
            int pct = (int)Math.Clamp(received * 100 / total, 0, 100);
            return $"发现新版本 {tagName}，正在下载 {pct}%（{FormatBytes(received)}/{FormatBytes(total)}）";
        }

        return $"发现新版本 {tagName}，正在下载 {FormatBytes(received)}";
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024):0.0} MB";
    }

    internal const string SilentSetupArgs =
        "/SILENT /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /SUPPRESSMSGBOXES";

    /// <summary>启动已下载的 Setup 静默安装。成功时 shouldExit 为 true，调用方必须退出。</summary>
    public bool TryApplyPending(out string message, out bool shouldExit)
    {
        shouldExit = false;
        var pending = Pending;
        if (pending == null || !File.Exists(pending.LocalPath))
        {
            message = "还没有已下载的更新";
            return false;
        }

        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = pending.LocalPath,
                Arguments = SilentSetupArgs,
                UseShellExecute = true
            });
            if (started == null)
            {
                message = "无法启动安装程序";
                return false;
            }

            RememberAutoApplyAttempt(pending.Version);
            shouldExit = true;
            message = "正在安装更新，程序将退出";
            DebugLog.Log($"已启动静默安装: {pending.LocalPath} ({pending.TagName})");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("应用更新失败", ex);
            RememberAutoApplyAttempt(pending.Version);
            message = ex is System.ComponentModel.Win32Exception
                ? "无法启动安装程序（可能已取消 UAC）"
                : "无法启动安装程序";
            return false;
        }
    }

    private bool IsAutoApplyDeferred(string version)
    {
        if (_paths == null)
        {
            return false;
        }

        string stamp = Path.Combine(_paths.UpdatesDir, AutoApplyStampFileName);
        if (!File.Exists(stamp))
        {
            return false;
        }

        try
        {
            string text = File.ReadAllText(stamp).Trim();
            string[] parts = text.Split('|');
            if (parts.Length < 2
                || !string.Equals(parts[0].Trim(), version, StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(parts[1].Trim(), out long ticks))
            {
                return false;
            }

            var when = new DateTime(ticks, DateTimeKind.Utc);
            return DateTime.UtcNow - when < AutoApplyDeferral;
        }
        catch
        {
            return false;
        }
    }

    private void RememberAutoApplyAttempt(string version)
    {
        if (_paths == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.UpdatesDir);
            File.WriteAllText(
                Path.Combine(_paths.UpdatesDir, AutoApplyStampFileName),
                version + "|" + DateTime.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("写入自动安装标记失败", ex);
        }
    }

    private void SetDownloadFailed(DownloadFailedInfo? failed)
    {
        DownloadFailed = failed;
        DownloadFailedChanged?.Invoke(failed);
    }

    /// <summary>在系统默认浏览器中打开指定下载链接或 Releases 页面。</summary>
    public static void OpenUrlInBrowser(string? url)
    {
        string target = string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            DebugLog.Log($"已在浏览器中打开链接: {target}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"打开浏览器链接失败: {target}", ex);
        }
    }

    private void TryRestorePending()
    {
        if (_paths == null || !Directory.Exists(_paths.UpdatesDir))
        {
            return;
        }

        try
        {
            const string prefix = "QuickClip-Setup-";
            PendingUpdate? best = null;
            foreach (string file in Directory.GetFiles(_paths.UpdatesDir, "QuickClip-Setup-*.exe"))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string versionPart = name.Substring(prefix.Length, name.Length - prefix.Length - 4);
                if (!IsNewer(versionPart, CurrentVersion) || new FileInfo(file).Length < 1024)
                {
                    continue;
                }

                if (best == null || IsNewer(versionPart, best.Version))
                {
                    best = new PendingUpdate
                    {
                        Version = versionPart,
                        TagName = "v" + versionPart,
                        LocalPath = file,
                        Channel = ReleaseChannel.Setup
                    };
                }
            }

            if (best != null)
            {
                SetPending(best);
                DebugLog.Log($"恢复已下载更新: {best.LocalPath}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("扫描已下载更新失败", ex);
        }
    }

    private void SetPending(PendingUpdate? pending)
    {
        Pending = pending;
        PendingChanged?.Invoke(pending);
    }

    private async Task<string?> DownloadReleaseAsync(ReleaseInfo release, AppPaths paths)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl) || !IsTrustedDownloadUrl(release.DownloadUrl))
        {
            DebugLog.Log("拒绝下载：地址不受信任");
            return null;
        }

        Directory.CreateDirectory(paths.UpdatesDir);
        string destName = $"QuickClip-Setup-{release.Version}.exe";
        string dest = Path.Combine(paths.UpdatesDir, destName);
        if (File.Exists(dest))
        {
            long existing = new FileInfo(dest).Length;
            if (release.AssetSize <= 0 || existing == release.AssetSize)
            {
                DebugLog.Log($"复用已下载更新: {destName} ({existing} 字节)");
                return dest;
            }
        }

        string part = dest + ".part";
        try
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            ApplyGitHubHeaders(request, api: false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                DebugLog.Log($"下载更新失败: HTTP {(int)response.StatusCode}");
                return null;
            }

            long? length = response.Content.Headers.ContentLength;
            if (length is > MaxDownloadBytes || release.AssetSize > MaxDownloadBytes)
            {
                DebugLog.Log($"拒绝下载：体积过大 length={length} asset={release.AssetSize}");
                return null;
            }

            long expected = length ?? (release.AssetSize > 0 ? release.AssetSize : 0);
            DebugLog.Log($"下载响应已到达: HTTP {(int)response.StatusCode} ContentLength={length} asset={release.AssetSize}");
            PublishDownloadProgress(release.TagName, 0, expected);

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                long total = 0;
                int lastLoggedPct = -1;
                var lastLogUtc = DateTime.UtcNow;
                while (true)
                {
                    int read;
                    using (var stallCts = new CancellationTokenSource(DownloadStallTimeout))
                    {
                        try
                        {
                            read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            DebugLog.Log($"下载停滞：{DownloadStallTimeout.TotalSeconds:0} 秒未收到数据（已收 {FormatBytes(total)}）");
                            return null;
                        }
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        DebugLog.Log("拒绝下载：超过体积上限");
                        return null;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
                    PublishDownloadProgress(release.TagName, total, expected);

                    int pct = expected > 0 ? (int)(total * 100 / expected) : -1;
                    var now = DateTime.UtcNow;
                    if ((pct >= 0 && pct / 10 > lastLoggedPct / 10) || now - lastLogUtc >= TimeSpan.FromSeconds(5))
                    {
                        lastLoggedPct = pct;
                        lastLogUtc = now;
                        DebugLog.Log(pct >= 0
                            ? $"下载进度 {pct}%（{FormatBytes(total)}/{FormatBytes(expected)}）"
                            : $"下载进度 {FormatBytes(total)}");
                    }
                }
            }

            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            File.Move(part, dest);
            CleanupOldUpdates(paths.UpdatesDir, dest);
            DebugLog.Log($"更新已下载: {destName}");
            return dest;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("下载更新失败", ex);
            try
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }

    private static void CleanupOldUpdates(string dir, string keep)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir, "QuickClip*.exe"))
            {
                if (!file.Equals(keep, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("清理旧更新包失败", ex);
        }
    }

    private static ReleaseInfo BuildReleaseInfo(GitHubReleaseDto dto)
    {
        string tag = dto.TagName!.Trim();
        string version = tag.TrimStart('v');
        var asset = PickAsset(dto.Assets, CurrentChannel);
        return new ReleaseInfo
        {
            Version = version,
            TagName = tag,
            DownloadUrl = asset?.BrowserDownloadUrl,
            AssetSize = asset?.Size ?? 0,
            Notes = dto.Body
        };
    }

    private static GitHubAssetDto? PickAsset(List<GitHubAssetDto>? assets, ReleaseChannel channel)
    {
        if (assets == null || assets.Count == 0)
        {
            return null;
        }

        static bool IsExe(GitHubAssetDto a) =>
            a.Name != null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        static bool IsSetup(GitHubAssetDto a) =>
            a.Name != null && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase);

        return assets.Where(IsExe).FirstOrDefault(IsSetup);
    }

    internal static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static void ApplyGitHubHeaders(HttpRequestMessage request, bool api)
    {
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            $"QuickClip/{CurrentVersion} (+https://github.com/{RepoOwner}/{RepoName})");
        if (api)
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            return;
        }

        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
    }

    private static string BuildDirectDownloadUrl(string tag) =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/QuickClip-Setup-win-x64.exe";

    private static bool TryParseTagFromLatestUrl(string? pathOrUrl, out string tag, out string version)
    {
        tag = string.Empty;
        version = string.Empty;
        if (string.IsNullOrEmpty(pathOrUrl))
        {
            return false;
        }

        const string marker = "/releases/tag/";
        int i = pathOrUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return false;
        }

        tag = pathOrUrl[(i + marker.Length)..].Trim().Trim('/');
        int q = tag.IndexOfAny(['?', '#']);
        if (q >= 0)
        {
            tag = tag[..q];
        }

        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        version = tag.TrimStart('v');
        return true;
    }

    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return one.Length <= 160 ? one : one[..160] + "…";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>比较两个版本号（x.y.z），candidate 大于 current 时为 true。</summary>
    private static bool IsNewer(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out var c) || !TryParseVersion(current, out var cur))
        {
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (c[i] != cur[i])
            {
                return c[i] > cur[i];
            }
        }

        return false;
    }

    private static bool TryParseVersion(string text, out int[] parts)
    {
        parts = new[] { 0, 0, 0 };
        string[] tokens = text.Split('.', '-');
        if (tokens.Length < 1)
        {
            return false;
        }

        for (int i = 0; i < 3 && i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i].Trim(), out int value))
            {
                return false;
            }

            parts[i] = value;
        }

        return true;
    }

    public void Dispose()
    {
        _scheduleTimer?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>更新检查结果状态。</summary>
public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Ready,
    Failed
}

/// <summary>一次更新检查的结果。</summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public ReleaseInfo? Release { get; init; }
    public PendingUpdate? Pending { get; init; }
    public string? Message { get; init; }

    public static UpdateCheckResult UpToDate() => new()
    {
        Status = UpdateCheckStatus.UpToDate,
        Message = $"当前已是最新版本 v{UpdateService.CurrentVersion}"
    };

    public static UpdateCheckResult UpdateAvailable(ReleaseInfo release) => new()
    {
        Status = UpdateCheckStatus.UpdateAvailable,
        Release = release,
        Message = $"发现新版本 {release.TagName}"
    };

    public static UpdateCheckResult Ready(PendingUpdate pending) => new()
    {
        Status = UpdateCheckStatus.Ready,
        Pending = pending,
        Message = $"新版本 {pending.TagName} 已下载"
    };

    public static UpdateCheckResult Fail(string message) => new()
    {
        Status = UpdateCheckStatus.Failed,
        Message = message
    };
}

/// <summary>一次更新检查的发布信息。</summary>
public sealed class ReleaseInfo
{
    public string Version { get; init; } = string.Empty;
    public string TagName { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public long AssetSize { get; init; }
    public string? Notes { get; init; }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
