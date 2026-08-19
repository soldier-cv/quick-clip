namespace QuickClip.Models;

/// <summary>应用外观主题（与 docs/themes-preview.html 对齐，不含 Dracula）。</summary>
public enum AppTheme
{
    /// <summary>中性灰黑 · 默认，接近终端（强调色：绿）。</summary>
    Terminal = 0,

    /// <summary>Atom / VS Code One Dark（列表第二位）。</summary>
    OneDark = 1,

    /// <summary>GitHub 深色。</summary>
    GitHubDark = 2,

    /// <summary>GitHub 浅色。</summary>
    GitHubLight = 3,

    /// <summary>早期效果图靛蓝。</summary>
    FluentIndigo = 4,

    /// <summary>北极冷色。</summary>
    Nord = 5,

    /// <summary>Catppuccin Mocha。</summary>
    Catppuccin = 6,

    /// <summary>Windows 11 官方暗黑（Zinc 深黑底 + Win11 天空蓝）。</summary>
    FluentDark = 7
}
