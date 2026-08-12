using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>
/// 渲染环境检测。
/// 注意：WPF Border 圆角与 Windows 10/11 无关，圆角由 WPF 自己画；
/// 仅当 Popup 关闭 AllowsTransparency 时才被迫用直角（不透明 HWND 只能是矩形）。
/// 检测应宁漏勿误杀，避免正常桌面也被关掉透明圆角预览。
/// </summary>
public static class RenderEnvironment
{
    /// <summary>
    /// 明确的远程/虚拟显示驱动特征（DriverDesc 子串）。
    /// 不用单独的 "virtual"/"idd"：会误伤 Hyper-V 残留、部分本机驱动描述。
    /// </summary>
    private static readonly string[] VirtualDriverKeywords =
    [
        "orayidd",
        "oray idd",
        "oray virtual",
        "huawei virtual display",
        "remote display adapter",
        "microsoft remote display",
        "parsec virtual display",
        "sunshine virtual",
        "usb display adapter",
        "displaylink",
        "todesk",
        "anydesk virtual",
        "rustdesk",
        "vnc mirror",
        "tightvnc",
        "spacedesk"
    ];

    private static bool? _cachedRemote;
    private static string? _matchedReason;

    /// <summary>匹配原因（日志用）。</summary>
    public static string? LastMatchReason => _matchedReason;

    /// <summary>
    /// 是否需要渲染降级（软件渲染 / 主窗不透明材质）。
    /// 真·RDP 会话，或注册表中明确的虚拟显示驱动。
    /// </summary>
    public static bool IsRemoteOrVirtualDisplay()
    {
        if (_cachedRemote is bool cached)
        {
            return cached;
        }

        _matchedReason = null;

        // 1) 当前就是远程桌面会话（mstsc 等）
        try
        {
            if (SystemInformation.TerminalServerSession)
            {
                _matchedReason = "TerminalServerSession";
                _cachedRemote = true;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        // 2) 显卡驱动描述命中白名单关键词
        try
        {
            const string displayClass =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var key = Registry.LocalMachine.OpenSubKey(displayClass);
            if (key != null)
            {
                foreach (string subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        string? desc = sub?.GetValue("DriverDesc") as string;
                        if (string.IsNullOrEmpty(desc))
                        {
                            continue;
                        }

                        foreach (string keyword in VirtualDriverKeywords)
                        {
                            if (desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                _matchedReason = $"DriverDesc={desc} (keyword={keyword})";
                                _cachedRemote = true;
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // 个别子键无权限时跳过
                    }
                }
            }
        }
        catch
        {
            // 注册表失败：按本机正常环境
        }

        _cachedRemote = false;
        return false;
    }

    /// <summary>
    /// 悬停预览是否必须用不透明直角 Popup。
    /// 仅真远程会话时强制；本机即使软件渲染也可 AllowsTransparency + 圆角。
    /// </summary>
    public static bool RequiresOpaquePopup()
    {
        try
        {
            if (SystemInformation.TerminalServerSession)
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
