using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace QuickClip.Services;

/// <summary>管理员权限检测与“以管理员身份重启”。</summary>
public static class AdminService
{
    /// <summary>当前进程是否以管理员权限运行。</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 UAC 以管理员权限重新启动自身。
    /// 返回 true 表示已成功拉起提权进程（调用方应退出当前实例）；
    /// 返回 false 表示用户取消 UAC 或启动失败（当前实例应继续运行）。
    /// </summary>
    public static bool RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            DebugLog.Log("以管理员身份重启失败：无法解析 ProcessPath");
            return false;
        }

        if (!File.Exists(exe))
        {
            DebugLog.Log($"以管理员身份重启失败：可执行文件不存在 {exe}");
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });

            // runas 成功时通常会返回非 null 进程；用户点“否”会抛 Win32Exception
            if (process == null)
            {
                DebugLog.Log("以管理员身份重启失败：Process.Start 返回 null");
                return false;
            }

            DebugLog.Log($"已请求以管理员身份重启: pid={process.Id}, path={exe}");
            return true;
        }
        catch (Exception ex)
        {
            // 用户取消 UAC（错误码 1223）或其它启动失败
            DebugLog.LogException("以管理员身份重启失败（可能已取消授权）", ex);
            return false;
        }
    }
}
