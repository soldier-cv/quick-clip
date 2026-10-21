using System.IO;
using QuickClip.Models;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class WindowOpenPositionTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qc-pos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SettingsService LoadWith(string json)
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);

        var settings = new SettingsService(path);
        settings.Load();
        return settings;
    }

    [Fact]
    public void Defaults_to_remember_last()
    {
        string dir = TempDir();
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "missing.json"));
            settings.Load();

            Assert.Equal(WindowOpenPosition.RememberLast, settings.WindowOpenPosition);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("RememberLast", WindowOpenPosition.RememberLast)]
    [InlineData("AtCursor", WindowOpenPosition.AtCursor)]
    [InlineData("RightSide", WindowOpenPosition.RightSide)]
    [InlineData("atcursor", WindowOpenPosition.AtCursor)]
    public void Parses_saved_position(string saved, WindowOpenPosition expected)
    {
        var settings = LoadWith($$"""{"WindowOpenPosition":"{{saved}}"}""");

        Assert.Equal(expected, settings.WindowOpenPosition);
    }

    [Fact]
    public void Unknown_value_falls_back_to_remember_last()
    {
        var settings = LoadWith("""{"WindowOpenPosition":"Somewhere"}""");

        Assert.Equal(WindowOpenPosition.RememberLast, settings.WindowOpenPosition);
    }

    [Fact]
    public void Legacy_false_remember_maps_to_right_side()
    {
        // 老配置只存 RememberWindowPosition=false，表示不复用位置
        var settings = LoadWith("""{"RememberWindowPosition":false}""");

        Assert.Equal(WindowOpenPosition.RightSide, settings.WindowOpenPosition);
    }

    [Fact]
    public void Legacy_true_remember_maps_to_remember_last()
    {
        var settings = LoadWith("""{"RememberWindowPosition":true}""");

        Assert.Equal(WindowOpenPosition.RememberLast, settings.WindowOpenPosition);
    }

    [Fact]
    public void New_field_wins_over_legacy_field()
    {
        var settings = LoadWith("""
            {"WindowOpenPosition":"AtCursor","RememberWindowPosition":false}
            """);

        Assert.Equal(WindowOpenPosition.AtCursor, settings.WindowOpenPosition);
    }

    [Fact]
    public void Position_round_trips_through_file()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var first = new SettingsService(path);
            first.Load();
            first.SetWindowOpenPosition(WindowOpenPosition.AtCursor);

            var second = new SettingsService(path);
            second.Load();

            Assert.Equal(WindowOpenPosition.AtCursor, second.WindowOpenPosition);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Setting_same_value_is_noop()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Load();
            settings.SetWindowOpenPosition(WindowOpenPosition.RightSide);
            settings.SetWindowOpenPosition(WindowOpenPosition.RightSide);

            Assert.Equal(WindowOpenPosition.RightSide, settings.WindowOpenPosition);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
