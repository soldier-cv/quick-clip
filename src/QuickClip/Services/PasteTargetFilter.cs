namespace QuickClip.Services;

/// <summary>
/// 判断一个顶层窗口是否适合作为粘贴目标。
/// 置顶模式下 Z-order 扫描会遇到工具窗口、任务栏、IME 候选框等，必须过滤。
/// </summary>
public static class PasteTargetFilter
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    public const int WS_DISABLED = 0x08000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CHILD = 0x40000000;
    public const int WS_CAPTION = 0x00C00000;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>系统/壳层窗口类，绝不能作为粘贴目标。</summary>
    private static readonly HashSet<string> BlockedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "Progman",
        "WorkerW",
        "ForegroundStaging",
        "DummyDWMListenerWindow",
        "EdgeUiInputTopWndClass",
        "NativeHWNDHost",
        "IME",
        "MSCTFIME UI",
        "CiceroUIWndFrame",
        "Windows.UI.Input.InputSite.WindowClass",
        "SysShadow",
        "tooltips_class32",
        "OleMainThreadWndClass",
        "Auto-Suggest Dropdown"
    };

    /// <summary>
    /// 类名 + 扩展样式是否构成可粘贴的外部应用窗口。
    /// 任务栏、IME、无激活工具窗、纯分层透明覆盖层一律排除。
    /// </summary>
    public static bool IsEligibleTarget(
        uint processId,
        uint ownProcessId,
        string? className,
        int style,
        int exStyle,
        bool isVisible,
        bool isIconic)
    {
        if (processId == 0 || processId == ownProcessId)
        {
            return false;
        }

        if (!isVisible || isIconic)
        {
            return false;
        }

        if ((style & WS_DISABLED) != 0)
        {
            return false;
        }

        if ((style & WS_CHILD) != 0)
        {
            return false;
        }

        if ((exStyle & WS_EX_NOACTIVATE) != 0)
        {
            return false;
        }

        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(className) && BlockedClassNames.Contains(className))
        {
            return false;
        }

        // 无标题栏的分层透明窗通常是覆盖层/HUD，不是可输入的编辑器。
        bool layeredTransparent =
            (exStyle & WS_EX_LAYERED) != 0 &&
            (exStyle & WS_EX_TRANSPARENT) != 0 &&
            (style & WS_CAPTION) == 0;
        if (layeredTransparent)
        {
            return false;
        }

        return true;
    }
}
