using System.IO;
using Microsoft.Win32;

namespace QuickClip.Services;

/// <summary>开机自启动：通过 HKCU 的 Run 注册表项实现（无需管理员权限）。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickClip";

    /// <summary>当前是否已开启自启动（同时校验 exe 仍存在）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value) && File.Exists(StripQuotes(value));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前注册表中的自启动路径（便于诊断）。</summary>
    public static string? GetRegisteredPath()
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
            string value = $"\"{exe}\"";
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
            {
                DebugLog.Log("启用开机自启动失败：无法打开 Run 键");
                return false;
            }

            key.SetValue(ValueName, value);

            // 回读校验，确保不是“只改了 UI”
            string? readBack = key.GetValue(ValueName) as string;
            bool ok = string.Equals(readBack, value, StringComparison.OrdinalIgnoreCase)
                      || (!string.IsNullOrEmpty(readBack) && File.Exists(StripQuotes(readBack)));
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

            // 回读校验已删除
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

    private static string StripQuotes(string value) => value.Trim().Trim('"');
}
