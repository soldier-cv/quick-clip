using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>
/// Windows 系统剪贴板历史记录管理服务。
/// 用于检测与配置 Windows 10/11 自带剪贴板历史功能，解除其对 Win+V 热键的独占占用。
/// 
/// @author xudong.hua,gemini
/// @since 2026-08-19 16:00 星期三
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
}
