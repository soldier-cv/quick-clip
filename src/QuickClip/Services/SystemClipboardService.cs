using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>
/// 接管前记录的系统状态快照：只有拿到「原始值」才能精确恢复，
/// 否则会把用户本来就关着的剪贴板历史强行打开、或删掉用户自己设置的 DisabledHotkeys 字母。
/// </summary>
public sealed class SystemClipboardSnapshot
{
    /// <summary>EnableClipboardHistory 原本是否存在（不存在表示跟随系统默认）。</summary>
    public bool HistoryValueExisted { get; set; }

    /// <summary>EnableClipboardHistory 原始 DWORD 值。</summary>
    public int? HistoryValue { get; set; }

    /// <summary>DisabledHotkeys 原本是否存在。</summary>
    public bool DisabledHotkeysValueExisted { get; set; }

    /// <summary>DisabledHotkeys 原始字符串。</summary>
    public string? DisabledHotkeysValue { get; set; }

    /// <summary>快照创建时间（UTC，便于排查）。</summary>
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Windows 系统剪贴板与 Win+V 热键的接管/恢复服务。
///
/// 接管前先把原始注册表状态写入 <c>%LOCALAPPDATA%\QuickClip\system-clipboard-snapshot.json</c>，
/// 退出、用户关闭接管、或卸载时按快照精确还原（含「值原本不存在」这种情况），
/// 不再无条件把 EnableClipboardHistory 写成 1、也不再无差别删除 DisabledHotkeys 里的 V。
/// </summary>
public static class SystemClipboardService
{
    /// <summary>Windows 系统剪贴板历史注册表项路径。</summary>
    private const string ClipboardRegistryKey = @"Software\Microsoft\Clipboard";

    /// <summary>控制系统剪贴板历史记录开关的键值名（1 为开启，0 为关闭）。</summary>
    private const string EnableClipboardHistoryValue = "EnableClipboardHistory";

    /// <summary>Windows 资源管理器高级设置注册表项路径（用于禁用特定 Win 快捷键）。</summary>
    private const string ExplorerAdvancedRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>资源管理器禁用热键字母列表键值名（例如包含 "V" 则 Explorer 不再注册 Win+V）。</summary>
    private const string DisabledHotkeysValue = "DisabledHotkeys";

    /// <summary>接管前状态快照文件名。</summary>
    public const string SnapshotFileName = "system-clipboard-snapshot.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ---------- 状态查询 ----------

    /// <summary>检查 Windows 自带剪贴板历史记录是否处于开启状态。</summary>
    public static bool IsClipboardHistoryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClipboardRegistryKey, false);
            if (key == null)
            {
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

    /// <summary>检查 Windows 资源管理器是否已在注册表中禁用了 Win+V 热键。</summary>
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

    // ---------- 接管 ----------

    /// <summary>
    /// 接管系统剪贴板：先快照原始状态，再关闭系统剪贴板历史并把 'V' 加入 Explorer DisabledHotkeys。
    /// 重复调用不会覆盖已有快照（始终以第一次接管前的状态为准）。
    /// </summary>
    public static bool EnsureSystemClipboardDisabled(AppPaths paths)
    {
        bool snapshotOk = CaptureSnapshotIfMissing(paths);
        bool clipboardOk = SetClipboardHistoryEnabled(false);
        bool hotkeyOk = SetWinVDisabledInExplorer(true);
        DebugLog.Log(
            $"系统剪贴板接管: snapshot={snapshotOk}, ClipboardDisabled={clipboardOk}, HotkeyDisabled={hotkeyOk}");
        return snapshotOk && clipboardOk && hotkeyOk;
    }

    /// <summary>
    /// 按快照精确恢复系统剪贴板与 Win+V 状态（退出 / 关闭接管 / 卸载时调用）。
    /// 没有快照时不做任何猜测性修改，只记录日志（由设置页的开关让用户手动恢复）。
    /// </summary>
    public static bool RestoreSystemClipboard(AppPaths paths)
    {
        string snapshotPath = GetSnapshotPath(paths);
        SystemClipboardSnapshot? snapshot = LoadSnapshot(snapshotPath);
        if (snapshot == null)
        {
            DebugLog.Log("没有系统剪贴板快照，跳过恢复（不做猜测性注册表修改）");
            return false;
        }

        bool historyOk = RestoreClipboardHistory(snapshot);
        bool hotkeyOk = RestoreDisabledHotkeys(snapshot);

        if (historyOk && hotkeyOk)
        {
            TryDeleteSnapshot(snapshotPath);
        }

        DebugLog.Log($"系统剪贴板状态已按快照恢复: history={historyOk}, hotkey={hotkeyOk}");
        return historyOk && hotkeyOk;
    }

    /// <summary>快照文件路径。</summary>
    public static string GetSnapshotPath(AppPaths paths) => Path.Combine(paths.BaseDir, SnapshotFileName);

    /// <summary>是否存在接管前的状态快照。</summary>
    public static bool HasSnapshot(AppPaths paths) => File.Exists(GetSnapshotPath(paths));

    private static bool CaptureSnapshotIfMissing(AppPaths paths)
    {
        string snapshotPath = GetSnapshotPath(paths);
        if (File.Exists(snapshotPath))
        {
            return true;
        }

        try
        {
            var snapshot = new SystemClipboardSnapshot();

            using (var key = Registry.CurrentUser.OpenSubKey(ClipboardRegistryKey, false))
            {
                object? value = key?.GetValue(EnableClipboardHistoryValue);
                if (value is int intValue)
                {
                    snapshot.HistoryValueExisted = true;
                    snapshot.HistoryValue = intValue;
                }
            }

            using (var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedRegistryKey, false))
            {
                if (key?.GetValue(DisabledHotkeysValue) is string text)
                {
                    snapshot.DisabledHotkeysValueExisted = true;
                    snapshot.DisabledHotkeysValue = text;
                }
            }

            Directory.CreateDirectory(paths.BaseDir);
            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));
            DebugLog.Log(
                $"已记录接管前系统剪贴板状态: historyExisted={snapshot.HistoryValueExisted}, " +
                $"history={snapshot.HistoryValue}, disabledHotkeys={snapshot.DisabledHotkeysValue ?? "(无)"}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("写入系统剪贴板快照失败", ex);
            return false;
        }
    }

    private static SystemClipboardSnapshot? LoadSnapshot(string snapshotPath)
    {
        try
        {
            if (!File.Exists(snapshotPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SystemClipboardSnapshot>(File.ReadAllText(snapshotPath), JsonOptions);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("读取系统剪贴板快照失败", ex);
            return null;
        }
    }

    private static void TryDeleteSnapshot(string snapshotPath)
    {
        try
        {
            File.Delete(snapshotPath);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("删除系统剪贴板快照失败", ex);
        }
    }

    private static bool RestoreClipboardHistory(SystemClipboardSnapshot snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClipboardRegistryKey, true);
            if (key == null)
            {
                return false;
            }

            if (snapshot.HistoryValueExisted && snapshot.HistoryValue is int value)
            {
                key.SetValue(EnableClipboardHistoryValue, value, RegistryValueKind.DWord);
            }
            else
            {
                // 原本没有这个值：删掉它才是真正的「还原」（保留 0 会永久改变系统默认行为）
                key.DeleteValue(EnableClipboardHistoryValue, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("恢复 Windows 剪贴板历史开关失败", ex);
            return false;
        }
    }

    private static bool RestoreDisabledHotkeys(SystemClipboardSnapshot snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedRegistryKey, true);
            if (key == null)
            {
                return false;
            }

            if (snapshot.DisabledHotkeysValueExisted && !string.IsNullOrEmpty(snapshot.DisabledHotkeysValue))
            {
                key.SetValue(DisabledHotkeysValue, snapshot.DisabledHotkeysValue, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(DisabledHotkeysValue, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("恢复 Explorer DisabledHotkeys 失败", ex);
            return false;
        }
    }

    // ---------- 单项写入（供设置页手动开关使用） ----------

    /// <summary>设置 Windows 自带剪贴板历史的开启/关闭状态。</summary>
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

    /// <summary>设置 Windows 资源管理器是否禁用 Win+V 热键（修改 DisabledHotkeys 键值）。</summary>
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
                    string updated = current.Replace("V", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("v", "", StringComparison.OrdinalIgnoreCase);
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
