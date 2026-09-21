using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class StackHudLayoutTests
{
    private static StackHudLayout.RectD Work => new(0, 0, 1920, 1040);

    [Fact]
    public void WindowWidthForPanel_adds_shadow_chrome()
    {
        Assert.Equal(468, StackHudLayout.WindowWidthForPanel(460));
        Assert.Equal(368, StackHudLayout.WindowWidthForPanel(360));
    }

    [Fact]
    public void ClampPanelWidth_keeps_default_and_respects_work_area()
    {
        Assert.Equal(460, StackHudLayout.ClampPanelWidth(460, 1920));
        Assert.Equal(360, StackHudLayout.ClampPanelWidth(200, 1920));
        Assert.Equal(900, StackHudLayout.ClampPanelWidth(1200, 1920));
        Assert.Equal(376, StackHudLayout.ClampPanelWidth(800, 400));
    }

    [Fact]
    public void DockToPanel_prefers_below_when_space_allows()
    {
        var panel = new StackHudLayout.RectD(1448, 130, 460, 780);
        var place = StackHudLayout.DockToPanel(panel, Work, 76);

        Assert.Equal(1448 - 4, place.Left);
        Assert.Equal(468, place.Width);
        Assert.Equal(panel.Bottom + 8 - 4, place.Top);
        Assert.True(place.PopupAbove);
    }

    [Fact]
    public void DockToPanel_flips_above_when_bottom_is_tight()
    {
        var panel = new StackHudLayout.RectD(1448, 800, 460, 220);
        var place = StackHudLayout.DockToPanel(panel, Work, 76);

        Assert.True(place.Top < panel.Y);
        Assert.False(place.PopupAbove);
        Assert.True(place.Top >= Work.Top);
    }

    [Fact]
    public void DockToPanel_stays_inside_work_area_when_panel_fills_screen()
    {
        var panel = new StackHudLayout.RectD(12, 12, 460, 1016);
        var place = StackHudLayout.DockToPanel(panel, Work, 76);

        Assert.True(place.Top >= Work.Top);
        Assert.True(place.Top + place.Height <= Work.Bottom);
        Assert.Equal(468, place.Width);
    }

    [Fact]
    public void ParkBottomRight_uses_same_width_as_panel()
    {
        var place = StackHudLayout.ParkBottomRight(Work, 520, 76);
        Assert.Equal(528, place.Width);
        Assert.Equal(Work.Right - 528, place.Left);
        Assert.True(place.PopupAbove);
    }
}
