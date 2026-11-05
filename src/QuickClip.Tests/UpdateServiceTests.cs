using System.Diagnostics;
using System.IO;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

/// <summary>
/// 更新版本判断回归测试：
/// 旧进程/旧安装包都不能被当成「新版本」，否则托盘会误报
/// 「发现新版本 X 已下载」，甚至把已升级的安装降级。
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.4.10", "1.4.11", false)]
    [InlineData("1.4.11", "1.4.11", false)]
    [InlineData("1.4.11.0", "1.4.11", false)]
    [InlineData("v1.4.11", "1.4.11", false)]
    [InlineData("1.4.11", "1.4.10", true)]
    [InlineData("1.4.12", "1.4.11", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    public void IsNewer_compares_numeric_parts(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("1.4.11.0", "1.4.11")]
    [InlineData("v1.4.11", "1.4.11")]
    [InlineData(" 1.4.11 ", "1.4.11")]
    [InlineData("1.4", "1.4.0")]
    public void NormalizeVersion_strips_padding_and_prefix(string text, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeVersion(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void NormalizeVersion_rejects_invalid_text(string text)
    {
        Assert.Null(UpdateService.NormalizeVersion(text));
    }

    [Fact]
    public void PickInstalledVersion_keeps_running_when_disk_is_older()
    {
        Assert.Equal(
            "1.4.11",
            UpdateService.PickInstalledVersion("1.4.11", exePath: null));
    }

    [Fact]
    public void PickInstalledVersion_prefers_newer_file_version()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickClip.exe");
        Assert.True(File.Exists(exe), "测试输出目录应包含 QuickClip.exe");

        string expected = UpdateService.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(exe).FileVersion)!;
        string picked = UpdateService.PickInstalledVersion("1.0.0", exe);

        Assert.Equal(expected, picked);
    }

    [Fact]
    public void MatchesFileVersion_only_accepts_the_expected_version()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickClip.exe");
        Assert.True(File.Exists(exe), "测试输出目录应包含 QuickClip.exe");

        string actual = UpdateService.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(exe).FileVersion)!;

        Assert.True(UpdateService.MatchesFileVersion(exe, actual));
        Assert.False(UpdateService.MatchesFileVersion(exe, "9.9.9"));
    }

    [Fact]
    public void FindRestorableUpdate_ignores_package_not_newer_than_installed()
    {
        using var dir = new TempUpdateDir();
        string package = dir.CreatePackage("1.4.10");

        PendingUpdate? pending = UpdateService.FindRestorableUpdate(new[] { package }, "1.4.11");

        Assert.Null(pending);
    }

    [Fact]
    public void FindRestorableUpdate_ignores_package_equal_to_installed()
    {
        using var dir = new TempUpdateDir();
        string package = dir.CreatePackage("1.4.11");

        PendingUpdate? pending = UpdateService.FindRestorableUpdate(new[] { package }, "1.4.11");

        Assert.Null(pending);
    }

    [Fact]
    public void FindRestorableUpdate_picks_highest_newer_package()
    {
        using var dir = new TempUpdateDir();
        string older = dir.CreatePackage("1.4.12");
        string newer = dir.CreatePackage("1.4.13");

        PendingUpdate? pending = UpdateService.FindRestorableUpdate(new[] { older, newer }, "1.4.11");

        Assert.NotNull(pending);
        Assert.Equal("1.4.13", pending!.Version);
        Assert.Equal("v1.4.13", pending.TagName);
        Assert.Equal(newer, pending.LocalPath);
    }

    [Fact]
    public void FindRestorableUpdate_ignores_tiny_broken_package()
    {
        using var dir = new TempUpdateDir();
        string tiny = Path.Combine(dir.DirectoryPath, "QuickClip-Setup-1.4.12.exe");
        File.WriteAllBytes(tiny, new byte[16]);

        PendingUpdate? pending = UpdateService.FindRestorableUpdate(new[] { tiny }, "1.4.11");

        Assert.Null(pending);
    }

    /// <summary>一次性临时更新目录，测试结束自动删除。</summary>
    private sealed class TempUpdateDir : IDisposable
    {
        public string DirectoryPath { get; }

        public TempUpdateDir()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "quickclip-update-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string CreatePackage(string version)
        {
            string file = Path.Combine(DirectoryPath, $"QuickClip-Setup-{version}.exe");
            File.WriteAllBytes(file, new byte[4096]);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论
            }
        }
    }
}
