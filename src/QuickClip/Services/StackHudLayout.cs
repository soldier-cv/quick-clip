namespace QuickClip.Services;

/// <summary>
/// 收集栈 HUD 相对主列表面板的跟宽与磁吸几何。
/// HUD 窗口含 12px 阴影边，视觉卡片应对齐面板外框。
/// </summary>
public static class StackHudLayout
{
    public const double Chrome = 4;
    public const double Gap = 8;
    public const double ParkGap = 4;
    public const double DefaultPanelWidth = 460;
    public const double MinPanelWidth = 360;
    public const double MaxPanelWidth = 900;
    public const double MinPanelHeight = 360;
    public const double DefaultPanelHeight = 780;

    public readonly record struct RectD(double X, double Y, double Width, double Height)
    {
        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    public readonly record struct Placement(double Left, double Top, double Width, double Height, bool PopupAbove);

    /// <summary>HUD 窗口宽度（含阴影边），使内部卡片外框等于面板宽度。</summary>
    public static double WindowWidthForPanel(double panelWidth) => panelWidth + Chrome * 2;

    public static double ClampPanelWidth(double width, double workAreaWidth, double margin = 12)
    {
        double max = Math.Min(MaxPanelWidth, Math.Max(MinPanelWidth, workAreaWidth - margin * 2));
        return Math.Clamp(width, MinPanelWidth, max);
    }

    public static double ClampPanelHeight(double height, double workAreaHeight, double margin = 12)
    {
        double max = Math.Max(MinPanelHeight, workAreaHeight - margin * 2);
        return Math.Clamp(height, MinPanelHeight, max);
    }

    /// <summary>面板可见时：宽度对齐列表，优先吸在下沿，空间不够翻到上沿。</summary>
    public static Placement DockToPanel(RectD panel, RectD workArea, double hudHeight)
    {
        double width = WindowWidthForPanel(panel.Width);
        double left = Clamp(panel.Left - Chrome, workArea.Left, workArea.Right - width);

        double visualHeight = Math.Max(1, hudHeight - Chrome * 2);
        double need = visualHeight + Gap;
        double spaceBelow = workArea.Bottom - panel.Bottom;
        double spaceAbove = panel.Top - workArea.Top;

        bool below;
        if (spaceBelow >= need)
        {
            below = true;
        }
        else if (spaceAbove >= need)
        {
            below = false;
        }
        else
        {
            below = spaceBelow >= spaceAbove;
        }

        double top;
        if (below)
        {
            top = panel.Bottom + Gap - Chrome;
        }
        else
        {
            top = panel.Top - Gap - visualHeight - Chrome;
        }

        top = Clamp(top, workArea.Top, workArea.Bottom - hudHeight);
        return new Placement(left, top, width, hudHeight, below);
    }

    /// <summary>面板隐藏时：保持跟列表同宽，停靠当前工作区右下角。</summary>
    public static Placement ParkBottomRight(RectD workArea, double panelWidth, double hudHeight)
    {
        double width = WindowWidthForPanel(panelWidth);
        double left = workArea.Right - width;
        double top = workArea.Bottom - hudHeight - ParkGap;
        left = Clamp(left, workArea.Left, workArea.Right - width);
        top = Clamp(top, workArea.Top, workArea.Bottom - hudHeight);
        return new Placement(left, top, width, hudHeight, true);
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
