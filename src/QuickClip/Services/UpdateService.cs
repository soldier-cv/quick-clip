using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace QuickClip.Services;

/// <summary>版本与更新检查：查询 GitHub Releases 最新版。</summary>
public sealed class UpdateService : IDisposable
{
    private const string RepoOwner = "soldier-cv";
    private const string RepoName = "quick-clip";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>当前程序版本（来自程序集版本号）。</summary>
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 检查 GitHub Releases 是否有更新版本。
    /// 网络/API 失败时 Status=Failed，不会伪装成“已是最新”。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.TryAddWithoutValidation("User-Agent", $"QuickClip/{CurrentVersion}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                DebugLog.Log($"检查更新失败: HTTP {code}");
                string detail = code == 404
                    ? "仓库尚无 Releases 或地址不可用"
                    : $"HTTP {code}";
                return UpdateCheckResult.Fail($"检查更新失败：{detail}。可手动访问 {ReleasesPageUrl}");
            }

            var dto = await response.Content.ReadFromJsonAsync<GitHubReleaseDto>();
            if (dto == null || string.IsNullOrEmpty(dto.TagName))
            {
                return UpdateCheckResult.Fail("检查更新失败：无法解析 GitHub 响应");
            }

            var release = new ReleaseInfo
            {
                Version = dto.TagName.TrimStart('v'),
                TagName = dto.TagName,
                DownloadUrl = dto.Assets?.FirstOrDefault()?.BrowserDownloadUrl ?? dto.HtmlUrl ?? ReleasesPageUrl,
                Notes = dto.Body
            };

            DebugLog.Log($"检查更新完成: 最新 {release.TagName}，当前 v{CurrentVersion}");
            if (IsNewer(release.Version, CurrentVersion))
            {
                return UpdateCheckResult.UpdateAvailable(release);
            }

            return UpdateCheckResult.UpToDate();
        }
        catch (Exception ex)
        {
            DebugLog.LogException("检查更新异常", ex);
            return UpdateCheckResult.Fail($"检查更新失败：{ex.Message}。可手动访问 {ReleasesPageUrl}");
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
        string[] tokens = text.Split('.');
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

    public void Dispose() => _http.Dispose();
}

/// <summary>更新检查结果状态。</summary>
public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

/// <summary>一次更新检查的结果。</summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public ReleaseInfo? Release { get; init; }
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
    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
