using System.IO;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class ClipboardBehaviorTests
{
    [Theory]
    [InlineData("C:\\a.txt", new[] { "C:\\a.txt" })]
    [InlineData("C:\\a.txt\r\nC:\\b.txt", new[] { "C:\\a.txt", "C:\\b.txt" })]
    [InlineData("C:\\a.txt\nC:\\b.txt", new[] { "C:\\a.txt", "C:\\b.txt" })]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    public void SplitFilePaths_accepts_both_newlines(string? text, string[] expected)
    {
        Assert.Equal(expected, ClipboardItem.SplitFilePaths(text));
    }

    [Fact]
    public void Search_keeps_pinyin_initials_for_chinese_query()
    {
        var item = new ClipboardItem { TextContent = "设计架构" };

        Assert.True(SearchService.IsMatch(item, "sjjg"));
        Assert.True(SearchService.IsMatch(item, "设计"));
        Assert.False(SearchService.IsMatch(item, "a"));
    }

    [Fact]
    public void Search_matches_note_text_and_its_pinyin_initials()
    {
        var item = new ClipboardItem { TextContent = "hello", Note = "周会材料" };

        Assert.True(SearchService.IsMatch(item, "周会"));
        Assert.True(SearchService.IsMatch(item, "zhcl"));
        Assert.False(SearchService.IsMatch(item, "zz"));
    }

    [Fact]
    public void Drag_payload_keeps_text_and_leaves_the_note_behind()
    {
        var data = DragPayload.Create(new ClipboardItem
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "只要正文",
            Note = "这是备注"
        });

        Assert.NotNull(data);
        Assert.Equal("只要正文", data!.GetText());
        Assert.DoesNotContain("备注", data.GetText());
    }

    [Fact]
    public void Drag_payload_drops_files_that_are_gone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "quickclip-drag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string kept = Path.Combine(dir, "keep.txt");
        File.WriteAllText(kept, "a");
        try
        {
            var data = DragPayload.Create(new ClipboardItem
            {
                ContentType = ClipboardContentType.File,
                TextContent = kept + "\r\n" + Path.Combine(dir, "missing.txt"),
                Note = "不要带上"
            });

            Assert.NotNull(data);
            var files = data!.GetFileDropList();
            Assert.NotNull(files);
            Assert.Single(files!);
            Assert.Equal(kept, files[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Drag_payload_is_empty_when_the_image_file_is_missing()
    {
        var data = DragPayload.Create(new ClipboardItem
        {
            ContentType = ClipboardContentType.Image,
            PreviewPath = Path.Combine(Path.GetTempPath(), "quickclip-missing-" + Guid.NewGuid().ToString("N") + ".png"),
            Note = "备注"
        });

        Assert.Null(data);
    }

    [Fact]
    public void Search_does_not_treat_latin_query_as_initials()
    {
        var item = new ClipboardItem { TextContent = "Apple 设计" };

        Assert.False(SearchService.IsMatch(item, "zz"));
        Assert.True(SearchService.IsMatch(item, "apple"));
        Assert.True(SearchService.IsMatch(item, "设计"));
    }

    [Fact]
    public void Stack_split_keeps_order_and_skips_blank_lines()
    {
        Assert.Equal(new[] { "第一行", "第二行" }, StackPasteService.SplitLines("第一行\r\n\r\n  第二行  \n"));
        Assert.Empty(StackPasteService.SplitLines("   "));
    }

    [Fact]
    public void Stack_split_expands_latest_text_and_leaves_files()
    {
        var items = new[]
        {
            new ClipboardItem { ContentType = ClipboardContentType.Text, TextContent = "先入栈\r\n也是多行" },
            new ClipboardItem { ContentType = ClipboardContentType.File, TextContent = "C:\\a.txt\r\nC:\\b.txt" },
            new ClipboardItem { ContentType = ClipboardContentType.Text, TextContent = "最后一条\n第二行" }
        };

        Assert.Equal(2, StackPasteService.FindSplittableIndex(items));
        Assert.Equal(-1, StackPasteService.FindSplittableIndex(
            [new ClipboardItem { ContentType = ClipboardContentType.File, TextContent = "C:\\a.txt\r\nC:\\b.txt" }]));
    }

    [Fact]
    public void Capture_does_not_drop_notification_while_self_writing()
    {
        Assert.False(ClipboardPipeline.ShouldDropClipboardNotification(capturePaused: false));
        Assert.True(ClipboardPipeline.ShouldDropClipboardNotification(capturePaused: true));
    }

    [Theory]
    [InlineData(ClipboardContentType.Text, "账号", true)]
    [InlineData(ClipboardContentType.Link, "https://example.com", true)]
    [InlineData(ClipboardContentType.Text, "", false)]
    [InlineData(ClipboardContentType.Image, "C:\\preview.png", false)]
    [InlineData(ClipboardContentType.File, "C:\\a.txt", false)]
    public void Stack_paste_passes_physical_ctrl_v_through_only_for_text(
        ClipboardContentType type, string? text, bool expected)
    {
        var item = new ClipboardItem { ContentType = type, TextContent = text };

        Assert.Equal(expected, StackPasteService.CanPassThroughPhysicalPaste(item));
        Assert.False(StackPasteService.CanPassThroughPhysicalPaste(null));
    }
}
