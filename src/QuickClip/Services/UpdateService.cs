using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace QuickClip.Services;

/// <summary>发布渠道：绿色单文件或依赖运行时的安装包。</summary>
public enum ReleaseChannel
{
    Portable,
    Setup
}

/// <summary>已下载、等待用户动手安装/替换的更新。</summary>
public sealed class PendingUpdate
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string LocalPath { get; init; }
    public required ReleaseChannel Channel { get; init; }
}

/// <summary>版本检查、按渠道下载、静默节流。不自动覆盖正在运行的进程。</summary>
public sealed class UpdateService : IDisposable
{
    private const string RepoOwner = "soldier-cv";
    private const string RepoName = "quick-clip";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string LatestReleasePageUrl = $"{ReleasesPageUrl}/latest";
    private const string InstalledMarkerFileName = "QuickClip.installed";
    private const long MaxDownloadBytes = 400L * 1024 * 1024;
    private static readonly TimeSpan SilentDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SilentInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _http = CreateHttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppPaths? _paths;
    private SettingsService? _settings;
    private System.Threading.Timer? _silentTimer;

    /// <summary>当前程序版本（来自程序集版本号）。</summary>
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>根据安装目录标记判断渠道：有 QuickClip.installed 为安装版。</summary>
    public static ReleaseChannel CurrentChannel { get; } = DetectChannel();

    public static string ChannelLabel =>
        CurrentChannel == ReleaseChannel.Setup ? "安装版" : "绿色版";

    public PendingUpdate? Pending { get; private set; }

    /// <summary>待安装更新变化（可能来自后台线程，订阅方需切回 UI）。</summary>
    public event Action<PendingUpdate?>? PendingChanged;

    /// <summary>需要提示用户时触发（title, message）。静默失败不触发。</summary>
    public event Action<string, string>? UserNotify;

    public void Attach(AppPaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
        paths.EnsureCreated();
        TryRestorePending();
    }

    /// <summary>启动约 90 秒后做一次静默检查；之后靠 24h 时间戳节流。</summary>
    public void StartSilentChecks()
    {
        _silentTimer?.Dispose();
        _silentTimer = new System.Threading.Timer(
            _ => _ = RunSilentCheckAsync(),
            null,
            SilentDelay,
            SilentInterval);
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

            using var cts = new CancellationTokenSource(ApiTimeout);
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

    /// <summary>检查并按当前渠道下载。interactive 为 false 时失败只写日志。</summary>
    public async Task<UpdateCheckResult> CheckAndDownloadAsync(bool interactive)
    {
        await _gate.WaitAsync();
        try
        {
            if (_paths != null &&
                Pending is { } existing &&
                File.Exists(existing.LocalPath) &&
                IsNewer(existing.Version, CurrentVersion))
            {
                return UpdateCheckResult.Ready(existing);
            }

            var check = await CheckForUpdateAsync();
            if (check.Status != UpdateCheckStatus.UpdateAvailable || check.Release == null)
            {
                if (interactive && check.Status == UpdateCheckStatus.Failed)
                {
                    UserNotify?.Invoke("QuickClip", check.Message ?? "检查更新失败");
                }

                return check;
            }

            if (_paths == null)
            {
                return UpdateCheckResult.Fail("内部错误：更新目录未初始化");
            }

            string? local = await DownloadReleaseAsync(check.Release, _paths);
            if (string.IsNullOrEmpty(local))
            {
                var fail = UpdateCheckResult.Fail($"发现新版本 {check.Release.TagName}，但下载失败");
                if (interactive)
                {
                    UserNotify?.Invoke("QuickClip", fail.Message ?? "下载失败");
                }

                return fail;
            }

            SetPending(new PendingUpdate
            {
                Version = check.Release.Version,
                TagName = check.Release.TagName,
                LocalPath = local,
                Channel = CurrentChannel
            });

            return UpdateCheckResult.Ready(Pending!);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 安装版：启动 Setup。绿色版：写临时脚本，退出后自动覆盖当前 exe 并重启。
    /// shouldExit 为 true 时调用方必须退出进程，否则文件被占用无法替换。
    /// </summary>
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
            if (pending.Channel == ReleaseChannel.Setup)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = pending.LocalPath,
                    UseShellExecute = true
                });
                message = "已启动安装程序";
                return true;
            }

            if (!TryStartPortableSwap(pending.LocalPath, out message))
            {
                return false;
            }

            shouldExit = true;
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("应用更新失败", ex);
            message = "无法开始更新";
            return false;
        }
    }

    /// <summary>启动隐藏脚本：等本进程退出后把新 exe 拷到原路径并拉起。</summary>
    private static bool TryStartPortableSwap(string sourcePath, out string message)
    {
        string? target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            message = "找不到当前程序路径";
            return false;
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            message = "更新文件与当前程序是同一份";
            return false;
        }

        string script = Path.Combine(Path.GetTempPath(), $"QuickClip-update-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, PortableSwapScript);
        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            Arguments = Quote(target) + " " + Quote(sourcePath) + " " + Environment.ProcessId,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        DebugLog.Log("已启动绿色版替换脚本");
        message = "正在更新并重启…";
        return true;
    }

    private static string Quote(string path) => "\"" + path.Trim('"') + "\"";

    // ASCII only：路径通过参数传入，避免脚本编码踩中文路径
    private const string PortableSwapScript =
        """
        @echo off
        setlocal EnableExtensions
        set "TARGET=%~1"
        set "SOURCE=%~2"
        set "WAITPID=%~3"
        if not exist "%SOURCE%" exit /b 1
        timeout /t 1 /nobreak >nul
        set /a n=0
        :wait
        tasklist /FI "PID eq %WAITPID%" /NH 2>nul | find /I "INFO:" >nul
        if %errorlevel%==0 goto copy
        set /a n+=1
        if %n% GEQ 45 goto copy
        timeout /t 1 /nobreak >nul
        goto wait
        :copy
        set /a n=0
        :copytry
        copy /Y "%SOURCE%" "%TARGET%" >nul
        if not errorlevel 1 goto launch
        set /a n+=1
        if %n% GEQ 20 goto launchsrc
        timeout /t 1 /nobreak >nul
        goto copytry
        :launch
        start "" "%TARGET%"
        goto cleanup
        :launchsrc
        start "" "%SOURCE%"
        :cleanup
        del "%~f0"
        """;

    private async Task RunSilentCheckAsync()
    {
        var settings = _settings;
        if (settings == null || !settings.AutoCheckUpdates)
        {
            return;
        }

        if (settings.LastUpdateCheckUtc is DateTime last &&
            DateTime.UtcNow - last < SilentInterval)
        {
            DebugLog.Log($"跳过静默检查：距上次 {(DateTime.UtcNow - last).TotalHours:0.0}h");
            return;
        }

        settings.SetLastUpdateCheckUtc(DateTime.UtcNow);
        var result = await CheckAndDownloadAsync(interactive: false);
        if (result.Status == UpdateCheckStatus.Ready && result.Pending != null)
        {
            UserNotify?.Invoke(
                "QuickClip",
                $"发现新版本 {result.Pending.TagName}，已下载。点击即可更新");
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
            string prefix = CurrentChannel == ReleaseChannel.Setup
                ? "QuickClip-Setup-"
                : "QuickClip-";
            PendingUpdate? best = null;
            foreach (string file in Directory.GetFiles(_paths.UpdatesDir, "*.exe"))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CurrentChannel == ReleaseChannel.Portable &&
                    name.StartsWith("QuickClip-Setup-", StringComparison.OrdinalIgnoreCase))
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
                        Channel = CurrentChannel
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
        string destName = CurrentChannel == ReleaseChannel.Setup
            ? $"QuickClip-Setup-{release.Version}.exe"
            : $"QuickClip-{release.Version}.exe";
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

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        DebugLog.Log("拒绝下载：超过体积上限");
                        return null;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
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

        var exes = assets.Where(IsExe).ToList();
        if (channel == ReleaseChannel.Setup)
        {
            return exes.FirstOrDefault(IsSetup);
        }

        return exes.FirstOrDefault(a =>
                   a.Name!.Equals("QuickClip.exe", StringComparison.OrdinalIgnoreCase))
               ?? exes.FirstOrDefault(a =>
                   a.Name!.Contains("portable", StringComparison.OrdinalIgnoreCase) && !IsSetup(a))
               ?? exes.FirstOrDefault(a => !IsSetup(a));
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
        CurrentChannel == ReleaseChannel.Setup
            ? $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/QuickClip-Setup-win-x64.exe"
            : $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/QuickClip.exe";

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

    private static ReleaseChannel DetectChannel()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir) &&
                File.Exists(Path.Combine(dir, InstalledMarkerFileName)))
            {
                return ReleaseChannel.Setup;
            }
        }
        catch
        {
            // ignore
        }

        return ReleaseChannel.Portable;
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
        _silentTimer?.Dispose();
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
