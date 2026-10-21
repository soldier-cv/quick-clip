using System.IO;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class ImageSaveServiceTests
{
    private static ClipboardItem Item(long id = 7) => new() { Id = id };

    [Fact]
    public void Default_pattern_uses_compact_date_and_index()
    {
        string name = ImageSaveService.BuildFileName(null, 3, Item(), null, ".png");
        string today = DateTime.Now.ToString("yyyyMMdd");

        Assert.Equal($"QC_{today}_3.png", name);
    }

    [Fact]
    public void Bare_date_tokens_are_expanded_without_touching_words()
    {
        string expanded = ImageSaveService.ExpandBareDateTokens(
            "QC_yyyyMMdd_HHmmss_Screenshot",
            new DateTime(2026, 10, 21, 9, 8, 7));

        Assert.Equal("QC_20261021_090807_Screenshot", expanded);
    }

    [Fact]
    public void Words_containing_date_letters_stay_literal()
    {
        string expanded = ImageSaveService.ExpandBareDateTokens("Address_add", new DateTime(2026, 10, 21));

        Assert.Equal("Address_add", expanded);
    }

    [Theory]
    [InlineData("{index}", "2")]
    [InlineData("{列表序号}", "2")]
    [InlineData("{序号}", "2")]
    [InlineData("{id}", "7")]
    public void Image_tokens_are_resolved(string pattern, string expected)
    {
        string name = ImageSaveService.BuildFileName(pattern, 2, Item(7), null, ".png");

        Assert.Equal(expected + ".png", name);
    }

    [Fact]
    public void Braced_date_pattern_is_supported()
    {
        string name = ImageSaveService.BuildFileName("QC_{yyyyMMdd}", 1, Item(), null, ".png");

        Assert.Equal($"QC_{DateTime.Now:yyyyMMdd}.png", name);
    }

    [Fact]
    public void Literal_list_index_words_match_the_documented_default()
    {
        string name = ImageSaveService.BuildFileName("QC_yyyyMMdd_列表序号", 4, Item(), null, ".png");

        Assert.Equal($"QC_{DateTime.Now:yyyyMMdd}_4.png", name);
    }

    [Fact]
    public void Snippet_placeholders_are_resolved()
    {
        string name = ImageSaveService.BuildFileName("shot_{date}", 1, Item(), null, ".png");

        Assert.Equal($"shot_{DateTime.Now:yyyy-MM-dd}.png", name);
    }

    [Fact]
    public void Extension_is_not_duplicated()
    {
        string name = ImageSaveService.BuildFileName("photo.png", 1, Item(), null, ".png");

        Assert.Equal("photo.png", name);
    }

    [Fact]
    public void Wrong_extension_in_pattern_is_replaced_by_source_format()
    {
        string name = ImageSaveService.BuildFileName("photo.jpg", 1, Item(), null, ".png");

        Assert.Equal("photo.png", name);
    }

    [Fact]
    public void Illegal_characters_are_sanitized()
    {
        string name = ImageSaveService.BuildFileName("a:b*c?d", 1, Item(), null, ".png");

        Assert.Equal("a_b_c_d.png", name);
    }

    [Fact]
    public void Unknown_extensions_fall_back_to_png()
    {
        Assert.Equal(".png", ImageSaveService.NormalizeExtension("gif"));
        Assert.Equal(".jpg", ImageSaveService.NormalizeExtension("JPG"));
    }

    [Fact]
    public void Unique_path_appends_suffix_instead_of_overwriting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "quickclip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string first = Path.Combine(dir, "a.png");
            File.WriteAllText(first, "x");

            string second = ImageSaveService.EnsureUniquePath(dir, "a.png");

            Assert.Equal(Path.Combine(dir, "a_2.png"), second);
            Assert.False(File.Exists(second));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Save_writes_real_file_and_never_overwrites()
    {
        string dir = Path.Combine(Path.GetTempPath(), "quickclip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "source.png");
            byte[] payload = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(source, payload);

            string saveDir = Path.Combine(dir, "out", "nested");
            string first = await ImageSaveService.SaveAsync(source, saveDir, "QC.png");
            string second = await ImageSaveService.SaveAsync(source, saveDir, "QC.png");

            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.NotEqual(first, second);
            Assert.Equal(payload, await File.ReadAllBytesAsync(second));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
