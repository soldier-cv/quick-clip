using QuickClip.Models;
using MediaColor = System.Windows.Media.Color;

namespace QuickClip.Services;

/// <summary>
/// 主题颜色令牌。悬停 / 选中底色按主题单独设计（非简单提亮），
/// 选中以强调色边框 + 专用底色表达，而非只改序号颜色。
/// </summary>
public sealed class ThemePalette
{
    public required AppTheme Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool IsDark { get; init; }

    public required MediaColor Canvas { get; init; }
    public required MediaColor Panel { get; init; }
    public required MediaColor Card { get; init; }
    /// <summary>悬停专用底色（与 Card 对比明显，非 +6 灰阶）。</summary>
    public required MediaColor CardHover { get; init; }
    /// <summary>选中专用底色（可带轻强调色调）。</summary>
    public required MediaColor CardSelected { get; init; }
    public required MediaColor Border { get; init; }
    public required MediaColor BorderStrong { get; init; }
    public required MediaColor Text { get; init; }
    public required MediaColor TextSecondary { get; init; }
    public required MediaColor TextMuted { get; init; }
    public required MediaColor Accent { get; init; }
    public required MediaColor AccentMuted { get; init; }
    public required MediaColor Pin { get; init; }
    public required MediaColor Search { get; init; }
    public required MediaColor Badge { get; init; }

    public static ThemePalette Get(AppTheme theme) => theme switch
    {
        AppTheme.FluentDark => FluentDark,
        AppTheme.GitHubDark => GitHubDark,
        AppTheme.GitHubLight => GitHubLight,
        AppTheme.FluentIndigo => FluentIndigo,
        AppTheme.Nord => Nord,
        AppTheme.Catppuccin => Catppuccin,
        AppTheme.OneDark => OneDark,
        _ => Terminal
    };

    public static ThemePalette Terminal { get; } = new()
    {
        Id = AppTheme.Terminal,
        DisplayName = "Terminal",
        Description = "中性灰黑 · 悬停抬升灰，选中墨绿底 + 绿边",
        IsDark = true,
        Canvas = Rgb(0x0D, 0x0D, 0x0D),
        Panel = Rgb(0x1C, 0x1C, 0x1C),
        Card = Rgb(0x24, 0x24, 0x24),
        CardHover = Rgb(0x33, 0x33, 0x33),
        CardSelected = Rgb(0x16, 0x2B, 0x1D),
        Border = Rgb(0x35, 0x35, 0x35),
        BorderStrong = Rgb(0x56, 0x56, 0x56),
        Text = Rgb(0xEA, 0xEA, 0xEA),
        TextSecondary = Rgb(0x9A, 0x9A, 0x9A),
        TextMuted = Rgb(0x6B, 0x6B, 0x6B),
        Accent = Rgb(0x3F, 0xB9, 0x50),
        AccentMuted = Rgba(0x3F, 0xB9, 0x50, 0x30),
        Pin = Rgb(0xD4, 0xA0, 0x17),
        Search = Rgb(0x13, 0x13, 0x13),
        Badge = Rgb(0x33, 0x33, 0x33)
    };

    public static ThemePalette FluentDark { get; } = new()
    {
        Id = AppTheme.FluentDark,
        DisplayName = "Fluent Dark",
        Description = "Windows 11 原生暗黑 · 沉浸深灰底，选中冰蓝底 + 天空蓝边",
        IsDark = true,
        Canvas = Rgb(0x18, 0x18, 0x18),
        Panel = Rgb(0x20, 0x20, 0x20),
        Card = Rgb(0x2B, 0x2B, 0x2B),
        CardHover = Rgb(0x38, 0x38, 0x38),
        CardSelected = Rgb(0x16, 0x30, 0x48),
        Border = Rgb(0x3B, 0x3B, 0x3B),
        BorderStrong = Rgb(0x55, 0x55, 0x55),
        Text = Rgb(0xFA, 0xFA, 0xFA),
        TextSecondary = Rgb(0xB8, 0xB8, 0xB8),
        TextMuted = Rgb(0x7D, 0x7D, 0x7D),
        Accent = Rgb(0x60, 0xCD, 0xFF),
        AccentMuted = Rgba(0x60, 0xCD, 0xFF, 0x33),
        Pin = Rgb(0xFC, 0xE1, 0x00),
        Search = Rgb(0x16, 0x16, 0x16),
        Badge = Rgb(0x38, 0x38, 0x38)
    };

    public static ThemePalette GitHubDark { get; } = new()
    {
        Id = AppTheme.GitHubDark,
        DisplayName = "GitHub Dark",
        Description = "GitHub 深色 · 悬停冷灰，选中海军蓝底 + 蓝边",
        IsDark = true,
        Canvas = Rgb(0x0D, 0x11, 0x17),
        Panel = Rgb(0x16, 0x1B, 0x22),
        Card = Rgb(0x21, 0x26, 0x2D),
        CardHover = Rgb(0x2D, 0x33, 0x3B),
        CardSelected = Rgb(0x0D, 0x28, 0x4A),
        Border = Rgb(0x30, 0x36, 0x3D),
        BorderStrong = Rgb(0x48, 0x4F, 0x58),
        Text = Rgb(0xE6, 0xED, 0xF3),
        TextSecondary = Rgb(0x8B, 0x94, 0x9E),
        TextMuted = Rgb(0x6E, 0x76, 0x81),
        Accent = Rgb(0x2F, 0x81, 0xF7),
        AccentMuted = Rgba(0x2F, 0x81, 0xF7, 0x33),
        Pin = Rgb(0xD2, 0x99, 0x22),
        Search = Rgb(0x0D, 0x11, 0x17),
        Badge = Rgb(0x30, 0x36, 0x3D)
    };

    public static ThemePalette GitHubLight { get; } = new()
    {
        Id = AppTheme.GitHubLight,
        DisplayName = "GitHub Light",
        Description = "GitHub 浅色 · 悬停压暗灰，选中淡蓝底 + 蓝边",
        IsDark = false,
        Canvas = Rgb(0xF6, 0xF8, 0xFA),
        Panel = Rgb(0xFF, 0xFF, 0xFF),
        Card = Rgb(0xF6, 0xF8, 0xFA),
        CardHover = Rgb(0xE4, 0xE9, 0xEF),
        CardSelected = Rgb(0xDD, 0xF4, 0xFF),
        Border = Rgb(0xD0, 0xD7, 0xDE),
        BorderStrong = Rgb(0x9A, 0xA4, 0xAE),
        Text = Rgb(0x1F, 0x23, 0x28),
        TextSecondary = Rgb(0x65, 0x6D, 0x76),
        TextMuted = Rgb(0x8C, 0x95, 0x9F),
        Accent = Rgb(0x09, 0x69, 0xDA),
        AccentMuted = Rgba(0x09, 0x69, 0xDA, 0x22),
        Pin = Rgb(0x9A, 0x67, 0x00),
        Search = Rgb(0xFA, 0xFB, 0xFD),
        Badge = Rgb(0xE7, 0xEB, 0xF0)
    };

    public static ThemePalette FluentIndigo { get; } = new()
    {
        Id = AppTheme.FluentIndigo,
        DisplayName = "Fluent Indigo",
        Description = "靛蓝 · 悬停蓝灰抬升，选中紫蓝底 + 靛边",
        IsDark = true,
        Canvas = Rgb(0x0B, 0x0F, 0x1A),
        Panel = Rgb(0x12, 0x18, 0x26),
        Card = Rgb(0x1A, 0x22, 0x34),
        CardHover = Rgb(0x28, 0x34, 0x4D),
        CardSelected = Rgb(0x1E, 0x24, 0x4A),
        Border = Rgb(0x28, 0x33, 0x46),
        BorderStrong = Rgb(0x3D, 0x4F, 0x6F),
        Text = Rgb(0xF1, 0xF5, 0xF9),
        TextSecondary = Rgb(0x94, 0xA3, 0xB8),
        TextMuted = Rgb(0x64, 0x74, 0x8B),
        Accent = Rgb(0x63, 0x66, 0xF1),
        AccentMuted = Rgba(0x63, 0x66, 0xF1, 0x38),
        Pin = Rgb(0xFB, 0xBF, 0x24),
        Search = Rgb(0x0E, 0x13, 0x1F),
        Badge = Rgb(0x28, 0x30, 0x50)
    };

    public static ThemePalette Nord { get; } = new()
    {
        Id = AppTheme.Nord,
        DisplayName = "Nord",
        Description = "Nord · 悬停极地灰，选中霜青底 + 青边",
        IsDark = true,
        Canvas = Rgb(0x2E, 0x34, 0x40),
        Panel = Rgb(0x3B, 0x42, 0x52),
        Card = Rgb(0x43, 0x4C, 0x5E),
        CardHover = Rgb(0x4E, 0x58, 0x6E),
        CardSelected = Rgb(0x35, 0x4B, 0x57),
        Border = Rgb(0x4A, 0x54, 0x68),
        BorderStrong = Rgb(0x5E, 0x6A, 0x7E),
        Text = Rgb(0xEC, 0xEF, 0xF4),
        TextSecondary = Rgb(0xD8, 0xDE, 0xE9),
        TextMuted = Rgb(0xA3, 0xB1, 0xC6),
        Accent = Rgb(0x88, 0xC0, 0xD0),
        AccentMuted = Rgba(0x88, 0xC0, 0xD0, 0x38),
        Pin = Rgb(0xEB, 0xCB, 0x8B),
        Search = Rgb(0x30, 0x36, 0x44),
        Badge = Rgb(0x4A, 0x54, 0x68)
    };

    public static ThemePalette Catppuccin { get; } = new()
    {
        Id = AppTheme.Catppuccin,
        DisplayName = "Catppuccin",
        Description = "摩卡 · 悬停浅紫灰，选中葡萄紫底 + 紫边",
        IsDark = true,
        Canvas = Rgb(0x11, 0x11, 0x1B),
        Panel = Rgb(0x1E, 0x1E, 0x2E),
        Card = Rgb(0x31, 0x32, 0x44),
        CardHover = Rgb(0x42, 0x44, 0x59),
        CardSelected = Rgb(0x35, 0x2B, 0x4B),
        Border = Rgb(0x43, 0x46, 0x59),
        BorderStrong = Rgb(0x58, 0x5B, 0x70),
        Text = Rgb(0xCD, 0xD6, 0xF4),
        TextSecondary = Rgb(0xA6, 0xAD, 0xC8),
        TextMuted = Rgb(0x6C, 0x70, 0x86),
        Accent = Rgb(0xCB, 0xA6, 0xF7),
        AccentMuted = Rgba(0xCB, 0xA6, 0xF7, 0x33),
        Pin = Rgb(0xF9, 0xE2, 0xAF),
        Search = Rgb(0x18, 0x18, 0x25),
        Badge = Rgb(0x43, 0x46, 0x59)
    };

    public static ThemePalette OneDark { get; } = new()
    {
        Id = AppTheme.OneDark,
        DisplayName = "One Dark",
        Description = "One Dark · 悬停编辑器灰，选中钢蓝底 + 蓝边",
        IsDark = true,
        Canvas = Rgb(0x21, 0x25, 0x2B),
        Panel = Rgb(0x28, 0x2C, 0x34),
        Card = Rgb(0x2E, 0x33, 0x3E),
        CardHover = Rgb(0x3E, 0x45, 0x53),
        CardSelected = Rgb(0x1F, 0x33, 0x4C),
        Border = Rgb(0x3C, 0x42, 0x4F),
        BorderStrong = Rgb(0x5C, 0x63, 0x70),
        Text = Rgb(0xAB, 0xB2, 0xBF),
        TextSecondary = Rgb(0x9D, 0xA5, 0xB4),
        TextMuted = Rgb(0x5C, 0x63, 0x70),
        Accent = Rgb(0x61, 0xAF, 0xEF),
        AccentMuted = Rgba(0x61, 0xAF, 0xEF, 0x33),
        Pin = Rgb(0xE5, 0xC0, 0x7B),
        Search = Rgb(0x1E, 0x22, 0x28),
        Badge = Rgb(0x3C, 0x42, 0x4F)
    };

    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        Terminal,
        FluentDark,
        OneDark,
        GitHubDark,
        GitHubLight,
        FluentIndigo,
        Nord,
        Catppuccin
    ];

    private static MediaColor Rgb(byte r, byte g, byte b) => MediaColor.FromRgb(r, g, b);

    private static MediaColor Rgba(byte r, byte g, byte b, byte a) => MediaColor.FromArgb(a, r, g, b);
}
