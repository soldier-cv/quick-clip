using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class AppFontServiceTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("系统默认", "")]
    [InlineData("  系统默认  ", "")]
    [InlineData("Microsoft YaHei UI", "Microsoft YaHei UI")]
    [InlineData("  Consolas  ", "Consolas")]
    public void NormalizeStoredName_maps_default_and_trims(string? input, string expected)
    {
        Assert.Equal(expected, AppFontService.NormalizeStoredName(input));
    }
}
