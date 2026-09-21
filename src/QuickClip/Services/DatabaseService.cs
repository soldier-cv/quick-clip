using System.IO;
using Microsoft.Data.Sqlite;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>SQLite 本地存储：剪贴板历史；超出条数上限时淘汰最旧非置顶。</summary>
public sealed class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前数据库文件路径（本地文件或网络 UNC 路径）。</summary>
    public string CurrentPath { get; }

    public DatabaseService(string dbPath)
    {
        CurrentPath = dbPath;
        try
        {
            _connection = OpenAndPrepare(dbPath);
        }
        catch (SqliteException ex) when (IsCorruptDatabase(ex))
        {
            // 数据库文件损坏时不让整个应用启动失败：改名备份后重建空库，
            // 用户历史丢失但应用可用，且原始文件保留在数据目录便于人工抢救。
            DebugLog.LogException($"数据库文件损坏，已备份并重建: {dbPath}", ex);
            string backup = BackupCorruptDatabase(dbPath);
            DebugLog.Log($"损坏数据库已备份为: {backup}");
            _connection = OpenAndPrepare(dbPath);
        }
    }

    private static SqliteConnection OpenAndPrepare(string dbPath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        try
        {
            connection.Open();
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>是否属于「文件不是数据库 / 已损坏」，而非临时占用或权限问题。</summary>
    private static bool IsCorruptDatabase(SqliteException ex) =>
        ex.SqliteErrorCode is 11 or 26 ||
        ex.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase);

    /// <summary>把损坏的库文件（连同 -wal/-shm）改名备份，返回备份路径。</summary>
    private static string BackupCorruptDatabase(string dbPath)
    {
        string backup = $"{dbPath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        try
        {
            if (File.Exists(dbPath))
            {
                File.Move(dbPath, backup);
            }

            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                string side = dbPath + suffix;
                if (File.Exists(side))
                {
                    try
                    {
                        File.Move(side, backup + suffix);
                    }
                    catch
                    {
                        // 边车文件搬不动不影响重建
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("备份损坏数据库失败", ex);
        }

        return backup;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS clipboard_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content_type TEXT NOT NULL,
                    text_content TEXT,
                    html_content TEXT,
                    rtf_content TEXT,
                    preview_path TEXT,
                    qr_content TEXT,
                    char_count INTEGER,
                    is_pinned INTEGER DEFAULT 0,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_items(created_at);

                CREATE TABLE IF NOT EXISTS snippets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    category TEXT NOT NULL DEFAULT '通用',
                    title TEXT NOT NULL,
                    content TEXT NOT NULL,
                    content_type TEXT NOT NULL DEFAULT 'text',
                    sort_order INTEGER DEFAULT 0,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_snippets_category ON snippets(category);
                """;
            cmd.ExecuteNonQuery();
        }

        // 老库补列（SQLite 不支持 ADD COLUMN IF NOT EXISTS）
        EnsureColumn(connection, "html_content", "TEXT");
        EnsureColumn(connection, "rtf_content", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection connection, string column, string type)
    {
        try
        {
            bool exists = false;
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(clipboard_items);";
                using var reader = probe.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
            {
                return;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE clipboard_items ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
            DebugLog.Log($"数据库已补充列: {column} {type}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"补充数据库列失败: {column}", ex);
        }
    }

    public async Task<long> InsertAsync(ClipboardItem item)
    {
        var (id, _) = await UpsertRecentAsync(item);
        return id;
    }

    /// <summary>
    /// 插入或提升至顶部（Bump to Top）：
    /// 若历史中已存在相同文本/文件内容，则更新其 created_at 为当前时间并提升到最新，避免重复历史堆积；
    /// 若不存在则执行正常 INSERT。返回 (id, isNew)。
    /// </summary>
    public async Task<(long Id, bool IsNew)> UpsertRecentAsync(ClipboardItem item)
    {
        await _gate.WaitAsync();
        try
        {
            // 文本、链接、文件类型查重提升
            if (item.ContentType is ClipboardContentType.Text or ClipboardContentType.Link or ClipboardContentType.File &&
                !string.IsNullOrEmpty(item.TextContent))
            {
                using var findCmd = _connection.CreateCommand();
                findCmd.CommandText = """
                    SELECT id, is_pinned, qr_content
                    FROM clipboard_items
                    WHERE content_type = $type AND text_content = $text
                    ORDER BY id DESC LIMIT 1;
                    """;
                findCmd.Parameters.AddWithValue("$type", item.ContentType.ToString());
                findCmd.Parameters.AddWithValue("$text", item.TextContent);

                using var reader = await findCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    long existingId = reader.GetInt64(0);
                    bool isPinned = reader.GetInt32(1) == 1;
                    string? qr = reader.IsDBNull(2) ? null : reader.GetString(2);
                    reader.Close();

                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.CommandText = """
                        UPDATE clipboard_items
                        SET created_at = $createdAt,
                            html_content = COALESCE($html, html_content),
                            rtf_content = COALESCE($rtf, rtf_content)
                        WHERE id = $id;
                        """;
                    updateCmd.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    updateCmd.Parameters.AddWithValue("$html", (object?)item.HtmlContent ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("$rtf", (object?)item.RtfContent ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("$id", existingId);
                    await updateCmd.ExecuteNonQueryAsync();

                    item.Id = existingId;
                    item.IsPinned = isPinned;
                    if (!string.IsNullOrEmpty(qr))
                    {
                        item.QrContent = qr;
                    }

                    return (existingId, false);
                }
            }

            // 若无重复项，执行正常插入
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clipboard_items
                    (content_type, text_content, html_content, rtf_content, preview_path,
                     qr_content, char_count, is_pinned, created_at)
                VALUES ($type, $text, $html, $rtf, $preview, $qr, $charCount, $pinned, $createdAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$type", item.ContentType.ToString());
            cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$html", (object?)item.HtmlContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rtf", (object?)item.RtfContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$preview", (object?)item.PreviewPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qr", (object?)item.QrContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$charCount", item.CharCount);
            cmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            item.Id = (long)(await cmd.ExecuteScalarAsync())!;
            return (item.Id, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>当前历史总条数（含置顶）。用于调整条数上限前确认会删掉多少条。</summary>
    public async Task<int> CountAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clipboard_items;";
            return (int)(long)(await cmd.ExecuteScalarAsync())!;
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
                SELECT id, content_type, text_content, html_content, rtf_content,
                       preview_path, qr_content, char_count, is_pinned, created_at
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

    public async Task UpdateQrContentAsync(long id, string qrContent)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_items SET qr_content = $qr WHERE id = $id;";
            cmd.Parameters.AddWithValue("$qr", qrContent);
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
            if (pinned)
            {
                cmd.CommandText = "UPDATE clipboard_items SET is_pinned = 1 WHERE id = $id;";
            }
            else
            {
                // 取消置顶时，排在除了置顶项的首位（作为最新的非置顶项）：
                // 时间更新为当前时间，且确保严格大于当前库中除自身外所有非置顶项的时间
                cmd.CommandText = """
                    UPDATE clipboard_items
                    SET is_pinned = 0,
                        created_at = CASE
                            WHEN (SELECT MAX(created_at) FROM clipboard_items WHERE is_pinned = 0 AND id != $id) >= $now
                            THEN (SELECT strftime('%Y-%m-%d %H:%M:%f', MAX(created_at), '+0.001 seconds') FROM clipboard_items WHERE is_pinned = 0 AND id != $id)
                            ELSE $now
                        END
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }

            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
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

    #region 常用短语 (Snippets)

    /// <summary>获取常用短语分类列表。</summary>
    public async Task<List<string>> GetSnippetCategoriesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // 首次访问时先落默认短语，保证分类筛选与列表数据一致
            await EnsureDefaultSnippetsNoLockAsync();

            var categories = new List<string> { "全部" };
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT category FROM snippets ORDER BY category;";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    string cat = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(cat) && !categories.Contains(cat))
                    {
                        categories.Add(cat);
                    }
                }
            }
            return categories;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>根据分类和搜索关键词获取常用短语列表。</summary>
    public async Task<List<SnippetItem>> GetSnippetsAsync(string? category = null, string? keyword = null)
    {
        await _gate.WaitAsync();
        try
        {
            // 如果为空，先插入默认的示例常用短语
            await EnsureDefaultSnippetsNoLockAsync();

            using var cmd = _connection.CreateCommand();
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(category) && category != "全部")
            {
                conditions.Add("category = @category");
                cmd.Parameters.AddWithValue("@category", category);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                conditions.Add("(title LIKE @kw OR content LIKE @kw)");
                cmd.Parameters.AddWithValue("@kw", $"%{keyword.Trim()}%");
            }

            string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            cmd.CommandText = $"SELECT id, category, title, content, content_type, sort_order, created_at FROM snippets {whereClause} ORDER BY sort_order ASC, id DESC;";

            var list = new List<SnippetItem>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new SnippetItem
                {
                    Id = reader.GetInt64(0),
                    Category = reader.IsDBNull(1) ? "通用" : reader.GetString(1),
                    Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Content = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ContentType = reader.IsDBNull(4) ? "text" : reader.GetString(4),
                    SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    CreatedAt = reader.IsDBNull(6) ? DateTime.Now : ParseDate(reader.GetString(6))
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureDefaultSnippetsNoLockAsync()
    {
        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM snippets;";
        var countObj = await checkCmd.ExecuteScalarAsync();
        if (countObj is long count && count > 0)
        {
            return;
        }

        var defaultSnippets = new[]
        {
            ("通用", "当前时间戳", "{datetime}", 1),
            ("通用", "当前日期", "{date}", 2),
            ("回复", "确认收到模板", "您好，已收到关于【{clipboard}】的反馈，正在处理中，感谢支持！", 3),
            ("办公", "邮件落款签名", "祝好！\n\n--\nQuickClip 用户\n{date}", 4)
        };

        foreach (var (cat, title, content, order) in defaultSnippets)
        {
            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO snippets (category, title, content, content_type, sort_order, created_at)
                VALUES (@cat, @title, @content, 'text', @order, CURRENT_TIMESTAMP);
                """;
            insertCmd.Parameters.AddWithValue("@cat", cat);
            insertCmd.Parameters.AddWithValue("@title", title);
            insertCmd.Parameters.AddWithValue("@content", content);
            insertCmd.Parameters.AddWithValue("@order", order);
            await insertCmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>新增常用短语。</summary>
    public async Task<long> AddSnippetAsync(SnippetItem item)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO snippets (category, title, content, content_type, sort_order, created_at)
                VALUES (@cat, @title, @content, @type, @order, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@cat", string.IsNullOrWhiteSpace(item.Category) ? "通用" : item.Category.Trim());
            cmd.Parameters.AddWithValue("@title", item.Title.Trim());
            cmd.Parameters.AddWithValue("@content", item.Content);
            cmd.Parameters.AddWithValue("@type", item.ContentType ?? "text");
            cmd.Parameters.AddWithValue("@order", item.SortOrder);

            var idObj = await cmd.ExecuteScalarAsync();
            long newId = idObj is long id ? id : 0;
            item.Id = newId;
            return newId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>更新常用短语。</summary>
    public async Task UpdateSnippetAsync(SnippetItem item)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE snippets
                SET category = @cat, title = @title, content = @content, content_type = @type, sort_order = @order
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@cat", string.IsNullOrWhiteSpace(item.Category) ? "通用" : item.Category.Trim());
            cmd.Parameters.AddWithValue("@title", item.Title.Trim());
            cmd.Parameters.AddWithValue("@content", item.Content);
            cmd.Parameters.AddWithValue("@type", item.ContentType ?? "text");
            cmd.Parameters.AddWithValue("@order", item.SortOrder);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除常用短语。</summary>
    public async Task DeleteSnippetAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM snippets WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion

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
            HtmlContent = reader.IsDBNull(3) ? null : reader.GetString(3),
            RtfContent = reader.IsDBNull(4) ? null : reader.GetString(4),
            PreviewPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            QrContent = reader.IsDBNull(6) ? null : reader.GetString(6),
            CharCount = reader.GetInt64(7),
            IsPinned = reader.GetInt64(8) != 0,
            CreatedAt = reader.IsDBNull(9) ? DateTime.Now : ParseDate(reader.GetString(9))
        };
    }

    private static DateTime ParseDate(string value)
    {
        return DateTime.TryParse(value, out var dt) ? dt : DateTime.Now;
    }
}



