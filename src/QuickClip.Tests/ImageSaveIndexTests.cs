using System.IO;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class ImageSaveIndexTests
{
    private sealed class MemoryStore : IImageSaveIndexStore
    {
        private readonly Dictionary<string, int> _map = new();

        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public Task<int> GetIndexAsync(string scopeKey)
        {
            Reads++;
            return Task.FromResult(_map.TryGetValue(scopeKey, out int v) ? v : 0);
        }

        public Task SetIndexAsync(string scopeKey, int index)
        {
            Writes++;
            _map[scopeKey] = Math.Max(_map.TryGetValue(scopeKey, out int v) ? v : 0, index);
            return Task.CompletedTask;
        }
    }

    private static ClipboardItem Item(long id = 1) => new() { Id = id };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qc-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> PrepareSourceAsync(string dir)
    {
        string source = Path.Combine(dir, "source.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        return source;
    }

    [Fact]
    public async Task Index_starts_at_one_and_increments_without_rescanning()
    {
        string dir = TempDir();
        string saveDir = Path.Combine(dir, "out");
        try
        {
            string source = await PrepareSourceAsync(dir);
            var store = new MemoryStore();
            string pattern = "QC_yyyyMMdd_{index}";
            var item = Item();

            var first = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);
            var second = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);
            var third = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);

            string today = DateTime.Now.ToString("yyyyMMdd");
            Assert.Equal($"QC_{today}_1.png", Path.GetFileName(first));
            Assert.Equal($"QC_{today}_2.png", Path.GetFileName(second));
            Assert.Equal($"QC_{today}_3.png", Path.GetFileName(third));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Clearing_all_files_does_not_restart_the_numbering()
    {
        string dir = TempDir();
        string saveDir = Path.Combine(dir, "out");
        try
        {
            string source = await PrepareSourceAsync(dir);
            var store = new MemoryStore();
            string pattern = "QC_yyyyMMdd_{index}";
            var item = Item();

            await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);
            await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);

            // 模拟用户清空导出目录（或清空剪贴板列表后重头开始）
            foreach (string f in Directory.GetFiles(saveDir))
            {
                File.Delete(f);
            }

            string next = await ImageSaveService.SaveWithIndexAsync(source, saveDir, pattern, item, store);
            string today = DateTime.Now.ToString("yyyyMMdd");
            Assert.Equal($"QC_{today}_3.png", Path.GetFileName(next));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_counter_record_falls_back_to_scanning_existing_files()
    {
        string dir = TempDir();
        string saveDir = Path.Combine(dir, "out");
        try
        {
            Directory.CreateDirectory(saveDir);
            string source = await PrepareSourceAsync(dir);
            string today = DateTime.Now.ToString("yyyyMMdd");

            // 目录里已有 7 张历史图片，但序号表是空的（例如升级前保存的）
            for (int i = 1; i <= 7; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(saveDir, $"QC_{today}_{i}.png"), [9]);
            }

            var store = new MemoryStore();
            string saved = await ImageSaveService.SaveWithIndexAsync(source, saveDir, "QC_yyyyMMdd_{index}", Item(), store);

            Assert.Equal($"QC_{today}_8.png", Path.GetFileName(saved));
            Assert.True(store.Writes > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task External_file_with_same_name_never_gets_overwritten()
    {
        string dir = TempDir();
        string saveDir = Path.Combine(dir, "out");
        try
        {
            Directory.CreateDirectory(saveDir);
            string source = await PrepareSourceAsync(dir);
            string today = DateTime.Now.ToString("yyyyMMdd");
            var store = new MemoryStore();

            await ImageSaveService.SaveWithIndexAsync(source, saveDir, "QC_yyyyMMdd_{index}", Item(), store);

            // 外部程序凭空塞入下一序号的文件
            string intruder = Path.Combine(saveDir, $"QC_{today}_2.png");
            await File.WriteAllTextAsync(intruder, "external");

            string saved = await ImageSaveService.SaveWithIndexAsync(source, saveDir, "QC_yyyyMMdd_{index}", Item(), store);

            Assert.Equal("external", await File.ReadAllTextAsync(intruder));
            Assert.NotEqual(intruder, saved);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Different_days_use_independent_counters()
    {
        string dir = TempDir();
        string saveDir = Path.Combine(dir, "out");
        try
        {
            string source = await PrepareSourceAsync(dir);
            var store = new MemoryStore();

            string scopeKey = ImageSaveService.BuildScopeKey(
                saveDir, $"QC_{DateTime.Now:yyyyMMdd}_", string.Empty, ".png");

            await store.SetIndexAsync(scopeKey, 5);
            Assert.Equal(5, await store.GetIndexAsync(scopeKey));

            // 换一天前缀就变了，键不同
            string otherDayKey = ImageSaveService.BuildScopeKey(
                saveDir, "QC_19990101_", string.Empty, ".png");
            Assert.Equal(0, await store.GetIndexAsync(otherDayKey));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scope_key_is_case_insensitive_on_directory()
    {
        string a = ImageSaveService.BuildScopeKey(@"C:\Shots", "QC_", string.Empty, ".png");
        string b = ImageSaveService.BuildScopeKey(@"c:\shots", "QC_", string.Empty, ".png");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Pattern_without_index_keeps_single_name()
    {
        string name = ImageSaveService.BuildFileName("shot", 1, Item(), null, ".png");

        Assert.Equal("shot.png", name);
    }
}
