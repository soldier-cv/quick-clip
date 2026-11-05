using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class UpdateStatusTextTests
{
    [Fact]
    public void Ready_file_wins_over_idle_activity()
    {
        var pending = new PendingUpdate
        {
            Version = "9.9.9",
            TagName = "v9.9.9",
            LocalPath = "x.exe",
            Channel = ReleaseChannel.Setup
        };

        string text = UpdateService.ResolveStatusText(
            pending,
            pendingFileExists: true,
            failed: null,
            activity: new UpdateActivity { Phase = UpdatePhase.Idle });

        Assert.Contains("v9.9.9", text);
        Assert.Contains("已下载就绪", text);
    }

    [Fact]
    public void Pending_older_than_installed_is_not_shown_as_ready()
    {
        var pending = new PendingUpdate
        {
            Version = "1.0.0",
            TagName = "v1.0.0",
            LocalPath = "x.exe",
            Channel = ReleaseChannel.Setup
        };

        string text = UpdateService.ResolveStatusText(
            pending,
            pendingFileExists: true,
            failed: null,
            activity: new UpdateActivity { Phase = UpdatePhase.Idle });

        Assert.DoesNotContain("已下载就绪", text);
    }

    [Fact]
    public void UpToDate_keeps_result_message()
    {
        string text = UpdateService.ResolveStatusText(
            pending: null,
            pendingFileExists: false,
            failed: null,
            activity: new UpdateActivity
            {
                Phase = UpdatePhase.UpToDate,
                Message = "当前已是最新版本 v1.4.3"
            });

        Assert.Equal("当前已是最新版本 v1.4.3", text);
    }

    [Fact]
    public void Downloading_keeps_progress_message()
    {
        string text = UpdateService.ResolveStatusText(
            pending: null,
            pendingFileExists: false,
            failed: null,
            activity: new UpdateActivity
            {
                Phase = UpdatePhase.Downloading,
                Message = "发现新版本 v1.4.4，正在下载 12%（1.0 MB/8.0 MB）",
                TagName = "v1.4.4"
            });

        Assert.Contains("正在下载 12%", text);
    }

    [Fact]
    public void Failed_download_uses_browser_hint()
    {
        var failed = new DownloadFailedInfo
        {
            Version = "1.4.4",
            TagName = "v1.4.4",
            DownloadUrl = "https://example.com/setup.exe",
            ErrorMessage = "网络异常",
            FailedTimeUtc = DateTime.UtcNow
        };

        string text = UpdateService.ResolveStatusText(
            pending: null,
            pendingFileExists: false,
            failed,
            activity: new UpdateActivity { Phase = UpdatePhase.Failed, Message = "网络异常" });

        Assert.Contains("自动下载失败", text);
        Assert.Contains("浏览器", text);
    }

    [Fact]
    public void Idle_without_message_does_not_clear()
    {
        string text = UpdateService.ResolveStatusText(
            pending: null,
            pendingFileExists: false,
            failed: null,
            activity: new UpdateActivity { Phase = UpdatePhase.Idle, Message = string.Empty });

        Assert.Equal(string.Empty, text);
    }
}
