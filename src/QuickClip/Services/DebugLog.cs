using System.IO;

namespace QuickClip.Services;

/// <summary>
/// 诊断日志：默认写入本地数据目录，按大小滚动保留有限份备份，避免占满用户磁盘。
/// 高频调试信息（如逐键回调）由环境变量 QUICKCLIP_DEBUG_LOG 控制。
/// 禁止写入 API Key、剪贴板正文、图片内容。
/// </summary>
public static class DebugLog
{
    /// <summary>单文件上限：超过后滚动到 debug.log.1。</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    /// <summary>保留的历史份数（debug.log.1 … debug.log.N），合计约 6MB。</summary>
    private const int BackupCount = 2;

    /// <summary>滚动持续失败导致膨胀时暂停写入。</summary>
    private const long HardCapBytes = 8 * 1024 * 1024;

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

    /// <summary>记录异常及完整堆栈，用于崩溃排查。会去掉 URL 查询串中的临时密钥。</summary>
    public static void LogException(string context, Exception ex)
    {
        Log($"[异常] {context}: {Sanitize(ex.ToString())}");
    }

    /// <summary>
    /// 只保留 scheme/host/path，去掉 query（魔搭 CDN 的 auth_key 等）。
    /// 解析失败则截断原文，避免把整段地址写进日志。
    /// </summary>
    public static string DescribeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(empty)";
        }

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        string one = url.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 160 ? one : one[..160] + "…";
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
    /// 滚动：debug.log → .1 → .2，超出份数删除。改名失败再回退复制+删除。
    /// </summary>
    private static void TryRollover(string path)
    {
        try
        {
            string oldest = path + "." + BackupCount;
            try
            {
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }
            }
            catch
            {
                // 最旧备份被占用时继续尝试前移
            }

            for (int i = BackupCount - 1; i >= 1; i--)
            {
                string from = path + "." + i;
                string to = path + "." + (i + 1);
                if (!File.Exists(from))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(to))
                    {
                        File.Delete(to);
                    }

                    File.Move(from, to);
                }
                catch
                {
                    // 单份失败不阻断后续滚动
                }
            }

            string firstBackup = path + ".1";
            try
            {
                if (File.Exists(firstBackup))
                {
                    File.Delete(firstBackup);
                }

                File.Move(path, firstBackup);
                return;
            }
            catch
            {
                // 改名失败（文件被占用）时回退为复制 + 删除
            }

            try
            {
                File.Copy(path, firstBackup, true);
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

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 去掉 URL query，避免 auth_key / token 进日志
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(https?://[^\s]+)\?[^\s]+",
            "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsDetailEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("QUICKCLIP_DEBUG_LOG");
        return value is "1" or "true" or "yes" or "on";
    }
}
