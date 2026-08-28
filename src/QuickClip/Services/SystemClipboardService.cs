using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>
/// Windows 系统剪贴板与全局热键接管管理服务。
/// 用于在程序启动时自动彻底禁用 Windows 自带剪贴板历史记录与资源管理器（Explorer）对 Win+V 热键的占用，
/// 并提供卸载/维护时的状态恢复能力。
/// 
/// @author xudong.hua,gemini
/// @since 2026-08-24 21:12 星期一
/// </summary>
public static class SystemClipboardService
{
    /// <summary>
    /// Windows 系统剪贴板历史注册表项路径
    /// </summary>
    private const string ClipboardRegistryKey = @"Software\Microsoft\Clipboard";

    /// <summary>
    /// 控制系统剪贴板历史记录开关的键值名（1 为开启，0 为关闭）
    /// </summary>
    private const string EnableClipboardHistoryValue = "EnableClipboardHistory";

    /// <summary>
    /// Windows 资源管理器高级设置注册表项路径（用于禁用特定 Win 快捷键）
    /// </summary>
    private const string ExplorerAdvancedRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>
    /// 资源管理器禁用热键字母列表键值名（例如包含 "V" 则 Explorer 不再注册 Win+V）
    /// </summary>
    private const string DisabledHotkeysValue = "DisabledHotkeys";

    /// <summary>
    /// 检查 Windows 自带剪贴板历史记录是否处于开启状态。
    /// </summary>
    /// <returns>若开启返回 true；关闭或未配置/读取异常返回 false。</returns>
    public static bool IsClipboardHistoryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClipboardRegistryKey, false);
            if (key == null)
            {
                // 若键不存在，通常表示未显式开启或由系统默认值决定
                return false;
            }

            object? value = key.GetValue(EnableClipboardHistoryValue);
            if (value is int intValue)
            {
                return intValue != 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("读取 Windows 剪贴板历史开关失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 检查 Windows 资源管理器是否已在注册表中禁用了 Win+V 热键。
    /// </summary>
    /// <returns>若 DisabledHotkeys 包含 'V' 或 'v' 则返回 true；否则返回 false。</returns>
    public static bool IsWinVHotkeyDisabledInExplorer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedRegistryKey, false);
            if (key == null)
            {
                return false;
            }

            object? value = key.GetValue(DisabledHotkeysValue);
            if (value is string disabledStr)
            {
                return disabledStr.Contains('V', StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("读取 Explorer DisabledHotkeys 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 启动时确保彻底接管：
    /// 1. 将系统剪贴板历史记录开关设为 0（禁用记录）；
    /// 2. 将 'V' 添加到 Explorer 的 DisabledHotkeys（使资源管理器彻底放弃 Win+V 热键注册）。
    /// </summary>
    /// <returns>操作是否整体成功</returns>
    public static bool EnsureSystemClipboardDisabled()
    {
        bool clipboardOk = SetClipboardHistoryEnabled(false);
        bool hotkeyOk = SetWinVDisabledInExplorer(true);
        DebugLog.Log($"已执行启动时系统剪贴板自动彻底接管: ClipboardDisabled={clipboardOk}, HotkeyDisabled={hotkeyOk}");
        return clipboardOk && hotkeyOk;
    }

    /// <summary>
    /// 恢复系统剪贴板与 Win+V 热键（供卸载或维护调用）：
    /// 1. 从 Explorer 的 DisabledHotkeys 中移除 'V'；
    /// 2. 恢复系统剪贴板历史开关为 1（开启）。
    /// </summary>
    /// <returns>操作是否整体成功</returns>
    public static bool RestoreSystemClipboard()
    {
        bool hotkeyOk = SetWinVDisabledInExplorer(false);
        bool clipboardOk = SetClipboardHistoryEnabled(true);
        DebugLog.Log($"已执行系统剪贴板与热键状态恢复: HotkeyRestored={hotkeyOk}, ClipboardRestored={clipboardOk}");
        return hotkeyOk && clipboardOk;
    }

    /// <summary>
    /// 设置 Windows 自带剪贴板历史记录的开启/关闭状态。
    /// 禁用后可释放系统对 Win+V 快捷键的独占注册，让 QuickClip 实现原生独占接管。
    /// </summary>
    /// <param name="enabled">true 开启，false 禁用</param>
    /// <returns>操作是否成功</returns>
    public static bool SetClipboardHistoryEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClipboardRegistryKey, true);
            if (key == null)
            {
                DebugLog.Log("无法打开或创建注册表项: " + ClipboardRegistryKey);
                return false;
            }

            key.SetValue(EnableClipboardHistoryValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            DebugLog.Log($"已更新 Windows 剪贴板历史开关 => {(enabled ? "开启" : "禁用")}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("写入 Windows 剪贴板历史开关失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 设置 Windows 资源管理器是否禁用 Win+V 热键（修改 DisabledHotkeys 键值）。
    /// </summary>
    /// <param name="disable">true 禁用 Win+V（注入 'V'），false 恢复 Win+V（移除 'V'）</param>
    /// <returns>操作是否成功</returns>
    public static bool SetWinVDisabledInExplorer(bool disable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedRegistryKey, true);
            if (key == null)
            {
                DebugLog.Log("无法打开或创建注册表项: " + ExplorerAdvancedRegistryKey);
                return false;
            }

            string current = key.GetValue(DisabledHotkeysValue) as string ?? string.Empty;
            bool containsV = current.Contains('V', StringComparison.OrdinalIgnoreCase);

            if (disable)
            {
                if (!containsV)
                {
                    string updated = current + "V";
                    key.SetValue(DisabledHotkeysValue, updated, RegistryValueKind.String);
                    DebugLog.Log($"已将 'V' 添加到 Explorer DisabledHotkeys => {updated}");
                }
            }
            else
            {
                if (containsV)
                {
                    string updated = current.Replace("V", "", StringComparison.OrdinalIgnoreCase).Replace("v", "", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(updated))
                    {
                        key.DeleteValue(DisabledHotkeysValue, false);
                        DebugLog.Log("已清空并删除 Explorer DisabledHotkeys");
                    }
                    else
                    {
                        key.SetValue(DisabledHotkeysValue, updated, RegistryValueKind.String);
                        DebugLog.Log($"已从 Explorer DisabledHotkeys 移除 'V' => {updated}");
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("更新 Explorer DisabledHotkeys 失败", ex);
            return false;
        }
    }
}
