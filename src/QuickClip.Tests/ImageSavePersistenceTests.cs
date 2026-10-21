using System.IO;
using Microsoft.Data.Sqlite;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class ImageSavePersistenceTests
{
    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "quickclip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Saved_path_is_stored_and_read_back()
    {
        string dir = CreateTempDir();
        string dbPath = Path.Combine(dir, "test.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var item = new ClipboardItem
            {
                ContentType = ClipboardContentType.Image,
                PreviewPath = @"C:\preview\a.png",
                CharCount = 10
            };

            await db.UpsertRecentAsync(item);
            var loaded = await db.GetRecentAsync(10);
            Assert.Single(loaded);
            Assert.Null(loaded[0].SavedPath);

            await db.UpdateSavedPathAsync(item.Id, @"C:\saved\QC_20261021_1.png");
            var reloaded = await db.GetRecentAsync(10);
            Assert.Equal(@"C:\saved\QC_20261021_1.png", reloaded[0].SavedPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Settings_round_trip_keeps_image_save_options()
    {
        string dir = CreateTempDir();
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var first = new SettingsService(path);
            first.Load();
            first.SetImageQuickSaveEnabled(true);
            first.SetImageSaveDirectory(@"D:\shots");
            first.SetImageFileNamePattern("QC_yyyyMMdd_{index}");

            var second = new SettingsService(path);
            second.Load();

            Assert.True(second.ImageQuickSaveEnabled);
            Assert.Equal(@"D:\shots", second.ImageSaveDirectory);
            Assert.Equal("QC_yyyyMMdd_{index}", second.ImageFileNamePattern);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Settings_fall_back_to_defaults_when_file_is_missing()
    {
        string dir = CreateTempDir();
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "missing.json"));
            settings.Load();

            Assert.False(settings.ImageQuickSaveEnabled);
            Assert.Equal(string.Empty, settings.ImageSaveDirectory);
            Assert.Equal(ImageSaveService.DefaultFileNamePattern, settings.ImageFileNamePattern);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_save_dir_is_isolated_from_preview_cache()
    {
        var paths = new AppPaths();

        Assert.NotEqual(paths.PreviewDir, paths.DefaultImageSaveDir);
        Assert.False(paths.DefaultImageSaveDir.StartsWith(paths.PreviewDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Empty_setting_resolves_to_the_default_directory()
    {
        var paths = new AppPaths();

        Assert.Equal(paths.DefaultImageSaveDir, ImageSaveService.ResolveDirectory(null, paths));
        Assert.Equal(paths.DefaultImageSaveDir, ImageSaveService.ResolveDirectory("   ", paths));
    }

    [Fact]
    public void Explicit_absolute_directory_wins_over_default()
    {
        var paths = new AppPaths();
        string custom = Path.Combine(Path.GetTempPath(), "qc-custom");

        Assert.Equal(custom, ImageSaveService.ResolveDirectory(custom, paths));
    }

    [Fact]
    public void Relative_or_preview_cache_directory_falls_back_to_default()
    {
        var paths = new AppPaths();

        Assert.Equal(paths.DefaultImageSaveDir, ImageSaveService.ResolveDirectory("relative\\dir", paths));
        Assert.Equal(paths.DefaultImageSaveDir, ImageSaveService.ResolveDirectory(paths.PreviewDir, paths));
        Assert.Equal(
            paths.DefaultImageSaveDir,
            ImageSaveService.ResolveDirectory(Path.Combine(paths.PreviewDir, "nested"), paths));
    }
}
