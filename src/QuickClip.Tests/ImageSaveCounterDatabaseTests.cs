using System.IO;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

/// <summary>
/// 用真实 SQLite 库验证序号表：跨重启、清空目录、外部文件冲突都不重号。
/// </summary>
public class ImageSaveCounterDatabaseTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qc-counter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Counter_survives_restart_and_keeps_incrementing()
    {
        string dir = TempDir();
        string dbPath = Path.Combine(dir, "test.db");
        string saveDir = Path.Combine(dir, "out");
        string source = Path.Combine(dir, "src.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        string pattern = "QC_yyyyMMdd_{index}";
        try
        {
            string first;
            using (var db = new DatabaseService(dbPath))
            {
                first = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, new ClipboardItem { Id = 1 }, db);
            }

            // 模拟程序重启：新建数据库连接，序号应从 2 继续
            using (var db2 = new DatabaseService(dbPath))
            {
                string second = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, new ClipboardItem { Id = 1 }, db2);
                string today = DateTime.Now.ToString("yyyyMMdd");

                Assert.Equal($"QC_{today}_1.png", Path.GetFileName(first));
                Assert.Equal($"QC_{today}_2.png", Path.GetFileName(second));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Clearing_export_folder_then_restarting_still_does_not_repeat()
    {
        string dir = TempDir();
        string dbPath = Path.Combine(dir, "test.db");
        string saveDir = Path.Combine(dir, "out");
        string source = Path.Combine(dir, "src.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        string pattern = "QC_yyyyMMdd_{index}";
        try
        {
            using (var db = new DatabaseService(dbPath))
            {
                await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, new ClipboardItem { Id = 1 }, db);
                await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, new ClipboardItem { Id = 1 }, db);
            }

            // 用户清空导出目录 + 重启程序
            foreach (string f in Directory.GetFiles(saveDir))
            {
                File.Delete(f);
            }

            using (var db = new DatabaseService(dbPath))
            {
                string next = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, new ClipboardItem { Id = 1 }, db);
                Assert.Equal($"QC_{DateTime.Now:yyyyMMdd}_3.png", Path.GetFileName(next));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Counter_value_is_monotonic()
    {
        string dir = TempDir();
        string dbPath = Path.Combine(dir, "test.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var day = new DateOnly(2026, 10, 21);

            await db.SetImageSaveIndexAsync(day, 5);
            await db.SetImageSaveIndexAsync(day, 3);

            Assert.Equal(5, await db.GetImageSaveIndexAsync(day));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Each_day_gets_its_own_row()
    {
        string dir = TempDir();
        string dbPath = Path.Combine(dir, "test.db");
        try
        {
            using var db = new DatabaseService(dbPath);

            await db.SetImageSaveIndexAsync(new DateOnly(2026, 10, 21), 4);
            await db.SetImageSaveIndexAsync(new DateOnly(2026, 10, 22), 1);

            Assert.Equal(4, await db.GetImageSaveIndexAsync(new DateOnly(2026, 10, 21)));
            Assert.Equal(1, await db.GetImageSaveIndexAsync(new DateOnly(2026, 10, 22)));
            Assert.Equal(0, await db.GetImageSaveIndexAsync(new DateOnly(2026, 10, 23)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
