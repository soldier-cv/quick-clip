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
}
