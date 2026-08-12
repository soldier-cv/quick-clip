using System.IO;

namespace QuickClip.Services;

/// <summary>
/// 诊断日志：默认写入本地数据目录，超过大小上限自动滚动保留最近一份。
/// 高频调试信息（如逐键回调）由环境变量 QUICKCLIP_DEBUG_LOG 控制，避免日志膨胀。
/// </summary>
public static class DebugLog
{
    /// <summary>滚动阈值：超过后当前日志改名保留为 debug.log.old，重新记录。</summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>磁盘保护上限：滚动持续失败导致日志异常膨胀时暂停写入，避免占满磁盘。</summary>
    private const long HardCapBytes = 25 * 1024 * 1024;

    private static readonly object Lock = new();
    private static readonly bool DetailEnabled = IsDetailEnabled();

    public static void Log(string message)
    {
        Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}");
    }

    /// <summary>高频调试信息，仅当 QUICKCLIP_DEBUG_LOG 为 1/true/yes/on 时写入。</summary>
    public static void LogDetail(string message)
    {
        if (DetailEnabled)
        {
            Log(message);
        }
    }

    /// <summary>记录异常及完整堆栈，用于崩溃排查。</summary>
    public static void LogException(string context, Exception ex)
    {
        Log($"[异常] {context}: {ex}");
    }

    private static void Write(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickClip");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "debug.log");

            lock (Lock)
            {
                long length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length > MaxLogBytes)
                {
                    TryRollover(path);
                    length = File.Exists(path) ? new FileInfo(path).Length : 0;
                }

                // 滚动持续失败导致日志异常膨胀时暂停写入，避免占满磁盘
                if (length > HardCapBytes)
                {
                    return;
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    /// <summary>
    /// 滚动日志：先清理旧备份，再把当前日志改名为 debug.log.old（改名失败回退为复制+删除）。
    /// 相比复制+删除，改名更原子，避免“备份成功但删除失败”导致日志无限膨胀。
    /// </summary>
    private static void TryRollover(string path)
    {
        try
        {
            string backup = path + ".old";
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch
            {
                // 旧备份被占用时忽略，继续尝试滚动
            }

            try
            {
                File.Move(path, backup);
                return;
            }
            catch
            {
                // 改名失败（文件被占用）时回退为复制 + 删除
            }

            try
            {
                File.Copy(path, backup, true);
                File.Delete(path);
            }
            catch
            {
                // 均失败：保持原文件继续追加，下一次写入再尝试滚动
            }
        }
        catch
        {
            // 滚动失败不影响继续记录
        }
    }

    private static bool IsDetailEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("QUICKCLIP_DEBUG_LOG");
        return value is "1" or "true" or "yes" or "on";
    }
}

