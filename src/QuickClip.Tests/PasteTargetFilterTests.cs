using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class PasteTargetFilterTests
{
    private const uint OwnPid = 1000;
    private const uint OtherPid = 2000;

    private static int VisibleCaptionStyle =>
        PasteTargetFilter.WS_VISIBLE | PasteTargetFilter.WS_CAPTION;

    [Fact]
    public void Accepts_visible_external_editor_like_notepadpp()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OtherPid,
            ownProcessId: OwnPid,
            className: "Notepad++",
            style: VisibleCaptionStyle,
            exStyle: 0,
            isVisible: true,
            isIconic: false);

        Assert.True(ok);
    }

    [Fact]
    public void Rejects_own_process()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OwnPid,
            ownProcessId: OwnPid,
            className: "HwndWrapper[QuickClip;;abc]",
            style: VisibleCaptionStyle,
            exStyle: 0,
            isVisible: true,
            isIconic: false);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_taskbar()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OtherPid,
            ownProcessId: OwnPid,
            className: "Shell_TrayWnd",
            style: PasteTargetFilter.WS_VISIBLE,
            exStyle: PasteTargetFilter.WS_EX_TOOLWINDOW,
            isVisible: true,
            isIconic: false);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_ime_candidate_window()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OtherPid,
            ownProcessId: OwnPid,
            className: "IME",
            style: PasteTargetFilter.WS_VISIBLE | PasteTargetFilter.WS_POPUP,
            exStyle: PasteTargetFilter.WS_EX_TOOLWINDOW | PasteTargetFilter.WS_EX_NOACTIVATE,
            isVisible: true,
            isIconic: false);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_minimized_or_hidden_window()
    {
        Assert.False(PasteTargetFilter.IsEligibleTarget(
            OtherPid, OwnPid, "Notepad++", VisibleCaptionStyle, 0, isVisible: false, isIconic: false));
        Assert.False(PasteTargetFilter.IsEligibleTarget(
            OtherPid, OwnPid, "Notepad++", VisibleCaptionStyle, 0, isVisible: true, isIconic: true));
    }

    [Fact]
    public void Accepts_uwp_application_frame()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OtherPid,
            ownProcessId: OwnPid,
            className: "ApplicationFrameWindow",
            style: VisibleCaptionStyle,
            exStyle: 0,
            isVisible: true,
            isIconic: false);

        Assert.True(ok);
    }

    [Fact]
    public void Rejects_noactivate_overlay()
    {
        bool ok = PasteTargetFilter.IsEligibleTarget(
            processId: OtherPid,
            ownProcessId: OwnPid,
            className: "XamlExplorerHostIslandWindow",
            style: PasteTargetFilter.WS_VISIBLE | PasteTargetFilter.WS_POPUP,
            exStyle: PasteTargetFilter.WS_EX_NOACTIVATE | PasteTargetFilter.WS_EX_TOOLWINDOW,
            isVisible: true,
            isIconic: false);

        Assert.False(ok);
    }
}
