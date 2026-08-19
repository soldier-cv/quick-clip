using System.IO;
using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>开机自启动：通过 HKCU 的 Run 注册表项实现（无需管理员权限）。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickClip";
    internal const string AutostartArgument = "--autostart";

    /// <summary>当前是否已开启自启动（同时校验 exe 仍存在）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            string? exe = ExtractExePath(GetRegisteredCommand());
            return !string.IsNullOrEmpty(exe) && File.Exists(exe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前注册表中的自启动命令行（便于诊断）。</summary>
    public static string? GetRegisteredPath() => GetRegisteredCommand();

    public static string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 仅安装版可改写 Run：把旧绿色版或失效路径改到当前 exe。
    /// 绿色版不得把已指向安装目录的键改回去。
    /// </summary>
    public static void MigrateInstalledAutostart()
    {
        if (!UpdateService.IsInstalledCopy())
        {
            return;
        }

        string? current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current) || !File.Exists(current))
        {
            return;
        }

        string? command = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        string? registeredExe = ExtractExePath(command);
        bool same = !string.IsNullOrEmpty(registeredExe)
                    && string.Equals(registeredExe, current, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(registeredExe);
        if (same)
        {
            if (command.Contains(AutostartArgument, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Enable();
            return;
        }

        DebugLog.Log($"安装版接管开机自启动: {command} -> {current}");
        Enable();
    }

    /// <summary>写入自启动注册表项。成功返回 true；失败不抛异常，返回 false。</summary>
    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            DebugLog.Log($"启用开机自启动失败：无效的 ProcessPath={exe}");
            return false;
        }

        try
        {
            string value = $"\"{exe}\" {AutostartArgument}";
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
            {
                DebugLog.Log("启用开机自启动失败：无法打开 Run 键");
                return false;
            }

            key.SetValue(ValueName, value);

            string? readBack = key.GetValue(ValueName) as string;
            string? readExe = ExtractExePath(readBack);
            bool ok = string.Equals(readExe, exe, StringComparison.OrdinalIgnoreCase)
                      && !string.IsNullOrEmpty(readExe)
                      && File.Exists(readExe);
            if (!ok)
            {
                DebugLog.Log($"启用开机自启动回读校验失败: wrote={value}, read={readBack}");
                return false;
            }

            DebugLog.Log($"已启用开机自启动: {value}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("启用开机自启动失败", ex);
            return false;
        }
    }

    /// <summary>删除自启动注册表项。成功或不存在均返回 true。</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);

            using var verify = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? still = verify?.GetValue(ValueName) as string;
            if (!string.IsNullOrEmpty(still))
            {
                DebugLog.Log($"禁用开机自启动回读校验失败: still={still}");
                return false;
            }

            DebugLog.Log("已禁用开机自启动");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("禁用开机自启动失败", ex);
            return false;
        }
    }

    /// <summary>从 Run 命令行取出 exe 路径（支持引号 + 参数）。</summary>
    internal static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string s = command.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1)
            {
                return s[1..end];
            }

            return s.Trim('"');
        }

        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }
}
