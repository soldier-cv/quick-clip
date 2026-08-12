using System.IO;
using Microsoft.Data.Sqlite;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>SQLite 本地存储：剪贴板历史；淘汰由条数上限 + 24h 超龄共同约束。</summary>
public sealed class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前数据库文件路径（本地文件或网络 UNC 路径）。</summary>
    public string CurrentPath { get; }

    public DatabaseService(string dbPath)
    {
        CurrentPath = dbPath;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content_type TEXT NOT NULL,
                text_content TEXT,
                preview_path TEXT,
                qr_content TEXT,
                char_count INTEGER,
                is_pinned INTEGER DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_items(created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<long> InsertAsync(ClipboardItem item)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clipboard_items
                    (content_type, text_content, preview_path, qr_content, char_count, is_pinned)
                VALUES ($type, $text, $preview, $qr, $charCount, $pinned);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$type", item.ContentType.ToString());
            cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$preview", (object?)item.PreviewPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qr", (object?)item.QrContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$charCount", item.CharCount);
            cmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
            item.Id = (long)(await cmd.ExecuteScalarAsync())!;
            return item.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<ClipboardItem>> GetRecentAsync(int limit = 300)
    {
        await _gate.WaitAsync();
        try
        {
            var items = new List<ClipboardItem>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, content_type, text_content, preview_path, qr_content,
                       char_count, is_pinned, created_at
                FROM clipboard_items
                ORDER BY is_pinned DESC, created_at DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TogglePinAsync(long id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_items SET is_pinned = $pinned WHERE id = $id;";
            cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除指定时间之前的非置顶条目。</summary>
    public async Task<int> DeleteOlderThanAsync(DateTime threshold)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_items WHERE is_pinned = 0 AND created_at < $threshold;";
            cmd.Parameters.AddWithValue("$threshold", threshold.ToString("yyyy-MM-dd HH:mm:ss"));
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按最大条数淘汰：保留全部置顶 + 最新的非置顶，使总数不超过 maxItems。
    /// 返回删除的 id 与 preview 路径，便于清理缓存/文件。
    /// </summary>
    public async Task<List<(long Id, string? PreviewPath)>> TrimToMaxItemsAsync(int maxItems)
    {
        if (maxItems < 1)
        {
            maxItems = 1;
        }

        await _gate.WaitAsync();
        try
        {
            long total;
            using (var countCmd = _connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM clipboard_items;";
                total = (long)(await countCmd.ExecuteScalarAsync())!;
            }

            long excess = total - maxItems;
            if (excess <= 0)
            {
                return new List<(long, string?)>();
            }

            var doomed = new List<(long Id, string? PreviewPath)>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT id, preview_path FROM clipboard_items
                    WHERE is_pinned = 0
                    ORDER BY created_at ASC, id ASC
                    LIMIT $n;
                    """;
                sel.Parameters.AddWithValue("$n", excess);
                using var reader = await sel.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(0);
                    string? preview = reader.IsDBNull(1) ? null : reader.GetString(1);
                    doomed.Add((id, preview));
                }
            }

            if (doomed.Count == 0)
            {
                return doomed;
            }

            using var del = _connection.CreateCommand();
            del.CommandText = $"DELETE FROM clipboard_items WHERE id IN ({string.Join(",", doomed.Select(d => d.Id))});";
            await del.ExecuteNonQueryAsync();
            return doomed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清除今日非置顶历史（本地日历日）。</summary>
    public async Task<int> DeleteTodayUnpinnedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM clipboard_items
                WHERE is_pinned = 0
                  AND substr(created_at, 1, 10) = $day;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 清空全部非置顶历史，置顶保留。
    /// 返回删除条数与 preview 路径，便于清理缩略图文件。
    /// </summary>
    public async Task<(int Count, List<string> PreviewPaths)> DeleteAllUnpinnedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var previews = new List<string>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT preview_path FROM clipboard_items
                    WHERE is_pinned = 0 AND preview_path IS NOT NULL AND preview_path != '';
                    """;
                using var reader = await sel.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } path)
                    {
                        previews.Add(path);
                    }
                }
            }

            using var del = _connection.CreateCommand();
            del.CommandText = "DELETE FROM clipboard_items WHERE is_pinned = 0;";
            int n = await del.ExecuteNonQueryAsync();
            return (n, previews);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清理数据库中不再引用的孤儿预览图片文件。</summary>
    public async Task CleanupOrphanPreviewsAsync(AppPaths paths)
    {
        await _gate.WaitAsync();
        List<string> referenced;
        try
        {
            referenced = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT preview_path FROM clipboard_items WHERE preview_path IS NOT NULL;";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) is { Length: > 0 } path)
                {
                    referenced.Add(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        var set = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(paths.PreviewDir))
            {
                if (!set.Contains(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略删除失败（文件可能正被占用）
                    }
                }
            }
        }
        catch
        {
            // 目录不存在等异常忽略
        }
    }

    /// <summary>压缩数据库文件，回收已删除记录占用的磁盘空间（每日执行一次即可）。</summary>
    public async Task VacuumAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _connection.Dispose();

    private static ClipboardItem ReadItem(SqliteDataReader reader)
    {
        return new ClipboardItem
        {
            Id = reader.GetInt64(0),
            ContentType = Enum.TryParse<ClipboardContentType>(reader.GetString(1), out var type)
                ? type
                : ClipboardContentType.Text,
            TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
            PreviewPath = reader.IsDBNull(3) ? null : reader.GetString(3),
            QrContent = reader.IsDBNull(4) ? null : reader.GetString(4),
            CharCount = reader.GetInt64(5),
            IsPinned = reader.GetInt64(6) != 0,
            CreatedAt = reader.IsDBNull(7) ? DateTime.Now : ParseDate(reader.GetString(7))
        };
    }

    private static DateTime ParseDate(string value)
    {
        return DateTime.TryParse(value, out var dt) ? dt : DateTime.Now;
    }
}



