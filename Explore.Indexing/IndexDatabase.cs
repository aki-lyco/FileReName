using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Explore.Indexing
{
    /// <summary>
    /// SQLite-based index database for files.
    /// DB path: %LOCALAPPDATA%\FileReName\index.db
    /// </summary>
    public sealed class IndexDatabase
    {
        // アプリ内の同時書き込みを直列化
        private static readonly SemaphoreSlim s_writeGate = new(1, 1);

        public static string DatabasePath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileReName");
                Directory.CreateDirectory(root);
                return Path.Combine(root, "index.db");
            }
        }

        private string _connectionString => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        public async Task EnsureCreatedAsync(CancellationToken ct = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Pragmas
            await using (var prag = conn.CreateCommand())
            {
                prag.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                await prag.ExecuteNonQueryAsync(ct);
            }

            const string SQL = @"
CREATE TABLE IF NOT EXISTS files(
  path        TEXT PRIMARY KEY,
  file_key    TEXT,
  parent      TEXT,
  name        TEXT,
  ext         TEXT,
  size        INTEGER,
  mtime       INTEGER,
  ctime       INTEGER,
  mime        TEXT,
  summary     TEXT,
  snippet     TEXT,
  tags        TEXT,       -- ★追加：JSON配列文字列
  classified  TEXT,
  indexed_at  INTEGER
);
CREATE INDEX IF NOT EXISTS idx_files_parent ON files(parent);
-- 互換のため保持（PRIMARY KEY と重複しても害なし）
CREATE UNIQUE INDEX IF NOT EXISTS idx_files_path ON files(path);

CREATE TABLE IF NOT EXISTS rename_suggestions(
  path        TEXT PRIMARY KEY,
  suggestion  TEXT,
  updated_at  INTEGER
);

-- ★AI分類結果（BLOCK F）
CREATE TABLE IF NOT EXISTS classification_suggestions(
  file_key   TEXT PRIMARY KEY,
  classified TEXT,
  confidence REAL
);
CREATE INDEX IF NOT EXISTS idx_clsug_classified ON classification_suggestions(classified);

-- ★移動履歴（BLOCK G）
CREATE TABLE IF NOT EXISTS moves(
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  file_key  TEXT,
  old_path  TEXT,
  new_path  TEXT,
  op        TEXT,   -- 'move' / 'copy+delete' など
  reason    TEXT,   -- 'classified' / 'fallback' など
  at        INTEGER
);
";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SQL;
            await cmd.ExecuteNonQueryAsync(ct);

            // 既存DBへの後方互換：tags列が無い場合に追加（失敗は無視）
            try
            {
                var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE files ADD COLUMN tags TEXT";
                await alter.ExecuteNonQueryAsync(ct);
            }
            catch { /* 既に列があるなどは無視 */ }
        }

        /// <summary>
        /// Bulk upsert records (insert new, update existing by path).
        /// Returns: (scanned count, inserted count).
        /// </summary>
        public async Task<(long scanned, long inserted)> BulkUpsertAsync(
            IAsyncEnumerable<DbFileRecord> records,
            int batchSize,
            IProgress<(long scanned, long inserted)>? progress,
            CancellationToken ct)
        {
            await EnsureCreatedAsync(ct);

            await s_writeGate.WaitAsync(ct); // アプリ内直列化
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);

                await using (var prag = conn.CreateCommand())
                {
                    prag.CommandText =
                        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                    await prag.ExecuteNonQueryAsync(ct);
                }

                long scanned = 0;
                long inserted = 0;

                var batch = new List<DbFileRecord>(Math.Max(1, batchSize));
                var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await foreach (var r in records.WithCancellation(ct))
                {
                    if (string.IsNullOrWhiteSpace(r.Path)) continue;
                    if (!seenInBatch.Add(r.Path)) continue; // バッチ内重複を間引き

                    batch.Add(r);
                    if (batch.Count >= batchSize)
                    {
                        var (s, i) = await UpsertBatchAsync(conn, batch, ct);
                        scanned += s; inserted += i;
                        progress?.Report((scanned, inserted));
                        batch.Clear();
                        seenInBatch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    var (s, i) = await UpsertBatchAsync(conn, batch, ct);
                    scanned += s; inserted += i;
                    progress?.Report((scanned, inserted));
                }

                return (scanned, inserted);
            }
            finally
            {
                s_writeGate.Release();
            }
        }

        /// <summary>
        /// 1件ずつ安全に UPSERT（INSERT OR IGNORE → UPDATE）。例外で止めない。
        /// ※ ここは同期 BeginTransaction/Commit を使用（DbTransaction 型不一致エラー回避）
        /// </summary>
        private static async Task<(int scanned, int inserted)> UpsertBatchAsync(
            SqliteConnection conn,
            List<DbFileRecord> batch,
            CancellationToken ct)
        {
            using var tx = conn.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // INSERT OR IGNORE
            var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
INSERT OR IGNORE INTO files
(path, file_key, parent, name, ext, size, mtime, ctime, mime, summary, snippet, tags, classified, indexed_at)
VALUES
($path, $key, $parent, $name, $ext, $size, $mtime, $ctime, $mime, $summary, $snippet, $tags, $classified, $at);";
            var i_path = insert.CreateParameter(); i_path.ParameterName = "$path"; insert.Parameters.Add(i_path);
            var i_key = insert.CreateParameter(); i_key.ParameterName = "$key"; insert.Parameters.Add(i_key);
            var i_parent = insert.CreateParameter(); i_parent.ParameterName = "$parent"; insert.Parameters.Add(i_parent);
            var i_name = insert.CreateParameter(); i_name.ParameterName = "$name"; insert.Parameters.Add(i_name);
            var i_ext = insert.CreateParameter(); i_ext.ParameterName = "$ext"; insert.Parameters.Add(i_ext);
            var i_size = insert.CreateParameter(); i_size.ParameterName = "$size"; insert.Parameters.Add(i_size);
            var i_mtime = insert.CreateParameter(); i_mtime.ParameterName = "$mtime"; insert.Parameters.Add(i_mtime);
            var i_ctime = insert.CreateParameter(); i_ctime.ParameterName = "$ctime"; insert.Parameters.Add(i_ctime);
            var i_mime = insert.CreateParameter(); i_mime.ParameterName = "$mime"; insert.Parameters.Add(i_mime);
            var i_summary = insert.CreateParameter(); i_summary.ParameterName = "$summary"; insert.Parameters.Add(i_summary);
            var i_snippet = insert.CreateParameter(); i_snippet.ParameterName = "$snippet"; insert.Parameters.Add(i_snippet);
            var i_tags = insert.CreateParameter(); i_tags.ParameterName = "$tags"; insert.Parameters.Add(i_tags);
            var i_classified = insert.CreateParameter(); i_classified.ParameterName = "$classified"; insert.Parameters.Add(i_classified);
            var i_at = insert.CreateParameter(); i_at.ParameterName = "$at"; insert.Parameters.Add(i_at);

            // UPDATE（既存/新規どちらにも適用）
            var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = @"
UPDATE files SET
  file_key   = $key,
  parent     = $parent,
  name       = $name,
  ext        = $ext,
  size       = $size,
  mtime      = $mtime,
  ctime      = $ctime,
  mime       = COALESCE($mime, mime),
  summary    = COALESCE($summary, summary),
  snippet    = COALESCE($snippet, snippet),
  tags       = COALESCE($tags, tags),
  classified = COALESCE($classified, classified),
  indexed_at = $at
WHERE path = $path;";
            var u_path = update.CreateParameter(); u_path.ParameterName = "$path"; update.Parameters.Add(u_path);
            var u_key = update.CreateParameter(); u_key.ParameterName = "$key"; update.Parameters.Add(u_key);
            var u_parent = update.CreateParameter(); u_parent.ParameterName = "$parent"; update.Parameters.Add(u_parent);
            var u_name = update.CreateParameter(); u_name.ParameterName = "$name"; update.Parameters.Add(u_name);
            var u_ext = update.CreateParameter(); u_ext.ParameterName = "$ext"; update.Parameters.Add(u_ext);
            var u_size = update.CreateParameter(); u_size.ParameterName = "$size"; update.Parameters.Add(u_size);
            var u_mtime = update.CreateParameter(); u_mtime.ParameterName = "$mtime"; update.Parameters.Add(u_mtime);
            var u_ctime = update.CreateParameter(); u_ctime.ParameterName = "$ctime"; update.Parameters.Add(u_ctime);
            var u_mime = update.CreateParameter(); u_mime.ParameterName = "$mime"; update.Parameters.Add(u_mime);
            var u_summary = update.CreateParameter(); u_summary.ParameterName = "$summary"; update.Parameters.Add(u_summary);
            var u_snippet = update.CreateParameter(); u_snippet.ParameterName = "$snippet"; update.Parameters.Add(u_snippet);
            var u_tags = update.CreateParameter(); u_tags.ParameterName = "$tags"; update.Parameters.Add(u_tags);
            var u_classified = update.CreateParameter(); u_classified.ParameterName = "$classified"; update.Parameters.Add(u_classified);
            var u_at = update.CreateParameter(); u_at.ParameterName = "$at"; update.Parameters.Add(u_at);

            int scanned = 0, inserted = 0;

            foreach (var r in batch)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                // 共通値
                var path = r.Path;
                var key = (object?)r.FileKey ?? DBNull.Value;
                var parent = (object?)r.Parent ?? DBNull.Value;
                var name = (object?)r.Name ?? DBNull.Value;
                var ext = (object?)r.Ext ?? DBNull.Value;
                var size = r.Size;
                var mtime = r.MTimeUnix;
                var ctime = r.CTimeUnix;
                var mime = (object?)r.Mime ?? DBNull.Value;
                var summary = (object?)r.Summary ?? DBNull.Value;
                var snippet = (object?)r.Snippet ?? DBNull.Value;
                var tags = (object?)r.Tags ?? DBNull.Value;
                var classified = (object?)r.Classified ?? DBNull.Value;

                // INSERT OR IGNORE（新規のみ 1 を返す）
                i_path.Value = path;
                i_key.Value = key;
                i_parent.Value = parent;
                i_name.Value = name;
                i_ext.Value = ext;
                i_size.Value = size;
                i_mtime.Value = mtime;
                i_ctime.Value = ctime;
                i_mime.Value = mime;
                i_summary.Value = summary;
                i_snippet.Value = snippet;
                i_tags.Value = tags;
                i_classified.Value = classified;
                i_at.Value = now;

                try
                {
                    var ins = await insert.ExecuteNonQueryAsync(ct);
                    if (ins == 1) inserted++;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE
                {
                    Debug.WriteLine($"[IndexDB] INSERT conflict at path: {path}");
                }

                // 必ず UPDATE（既存/新規に関わらず最新値で上書き）
                u_path.Value = i_path.Value;
                u_key.Value = i_key.Value;
                u_parent.Value = i_parent.Value;
                u_name.Value = i_name.Value;
                u_ext.Value = i_ext.Value;
                u_size.Value = i_size.Value;
                u_mtime.Value = i_mtime.Value;
                u_ctime.Value = i_ctime.Value;
                u_mime.Value = i_mime.Value;
                u_summary.Value = i_summary.Value;
                u_snippet.Value = i_snippet.Value;
                u_tags.Value = i_tags.Value;
                u_classified.Value = i_classified.Value;
                u_at.Value = now;

                await update.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return (scanned, inserted);
        }

        // ===== 単発 UPSERT ヘルパー（JsonImporter 等から呼ばれる） =====

        public async Task UpsertFileAsync(DbFileRecord r, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await s_writeGate.WaitAsync(ct);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                using var tx = conn.BeginTransaction();

                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO files
(path, file_key, parent, name, ext, size, mtime, ctime, mime, summary, snippet, tags, classified, indexed_at)
VALUES
($path, $key, $parent, $name, $ext, $size, $mtime, $ctime, $mime, $summary, $snippet, $tags, $classified, $at)
ON CONFLICT(path) DO UPDATE SET
  file_key   = excluded.file_key,
  parent     = excluded.parent,
  name       = excluded.name,
  ext        = excluded.ext,
  size       = excluded.size,
  mtime      = excluded.mtime,
  ctime      = excluded.ctime,
  mime       = COALESCE(excluded.mime, files.mime),
  summary    = COALESCE(excluded.summary, files.summary),
  snippet    = COALESCE(excluded.snippet, files.snippet),
  tags       = COALESCE(excluded.tags, files.tags),
  classified = COALESCE(excluded.classified, files.classified),
  indexed_at = excluded.indexed_at;";
                cmd.Parameters.AddWithValue("$path", r.Path);
                cmd.Parameters.AddWithValue("$key", (object?)r.FileKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$parent", (object?)r.Parent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$name", (object?)r.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ext", (object?)r.Ext ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$size", r.Size);
                cmd.Parameters.AddWithValue("$mtime", r.MTimeUnix);
                cmd.Parameters.AddWithValue("$ctime", r.CTimeUnix);
                cmd.Parameters.AddWithValue("$mime", (object?)r.Mime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$summary", (object?)r.Summary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$snippet", (object?)r.Snippet ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tags", (object?)r.Tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$classified", (object?)r.Classified ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await cmd.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
            finally
            {
                s_writeGate.Release();
            }
        }

        public async Task UpsertClassificationAsync(string path, string? classified, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await s_writeGate.WaitAsync(ct);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                using var tx = conn.BeginTransaction();

                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO files (path, classified, indexed_at)
VALUES ($path, $classified, $at)
ON CONFLICT(path) DO UPDATE SET
  classified = $classified,
  indexed_at = $at;";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$classified", (object?)classified ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await cmd.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
            finally
            {
                s_writeGate.Release();
            }
        }

        public async Task UpsertRenameSuggestionAsync(string path, string? suggestion, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await s_writeGate.WaitAsync(ct);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                using var tx = conn.BeginTransaction();

                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO rename_suggestions (path, suggestion, updated_at)
VALUES ($path, $sugg, $at)
ON CONFLICT(path) DO UPDATE SET
  suggestion = $sugg,
  updated_at = $at;";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$sugg", (object?)suggestion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await cmd.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
            finally
            {
                s_writeGate.Release();
            }
        }

        // ===== BLOCK F/G 追加メソッド =====

        public async Task UpsertClassificationSuggestionAsync(string fileKey, string classifiedRelPath, double confidence)
        {
            await EnsureCreatedAsync();
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO classification_suggestions(file_key, classified, confidence)
VALUES($k,$c,$p)
ON CONFLICT(file_key) DO UPDATE SET classified=excluded.classified, confidence=excluded.confidence;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$c", classifiedRelPath);
            cmd.Parameters.AddWithValue("$p", confidence);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UpdateSummaryAndSnippetAsync(string fileKey, string? summary, string? snippet)
        {
            await EnsureCreatedAsync();
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE files SET summary=COALESCE($s, summary), snippet=COALESCE($n, snippet) WHERE file_key=$k;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$s", (object?)summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", (object?)snippet ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>summary / snippet / tags をまとめて更新（空文字もそのまま反映、indexed_at も更新）</summary>
        public async Task UpdateSummarySnippetTagsAsync(string fileKey, string? summary, string? snippet, string? tagsJson, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE files
                                SET summary=$s, snippet=$n, tags=$t, indexed_at=$at
                                WHERE file_key=$k;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$s", (object?)summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", (object?)snippet ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)tagsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateFilePathAsync(string fileKey, string newPath, string newParent, string newName, string newExt, CancellationToken ct)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE files SET path=$p,parent=$parent,name=$name,ext=$ext,indexed_at=$at WHERE file_key=$k;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$p", newPath);
            cmd.Parameters.AddWithValue("$parent", newParent);
            cmd.Parameters.AddWithValue("$name", newName);
            cmd.Parameters.AddWithValue("$ext", newExt);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpsertFileFromFsAsync(string fullPath, CancellationToken ct)
        {
            var fi = new FileInfo(fullPath);
            var r = new DbFileRecord
            {
                FileKey = FileKeyUtil.GetStableKey(fi.FullName),
                Path = fi.FullName,
                Parent = fi.DirectoryName,
                Name = fi.Name,
                Ext = fi.Extension,
                Size = fi.Exists ? fi.Length : 0,
                MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                Mime = null,
                Summary = null,
                Snippet = null,
                Tags = null,       // ★追加
                Classified = null,
                IndexedAt = 0
            };
            await UpsertFileAsync(r, ct);
        }

        public async Task InsertMoveAsync(string fileKey, string oldPath, string newPath, string op, string reason, CancellationToken ct)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO moves(file_key, old_path, new_path, op, reason, at) VALUES($k,$o,$n,$op,$r,$t);";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$o", oldPath);
            cmd.Parameters.AddWithValue("$n", newPath);
            cmd.Parameters.AddWithValue("$op", op);
            cmd.Parameters.AddWithValue("$r", reason);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MigrateSuggestionsAsync(string oldFileKey, string newFileKey, CancellationToken ct)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();

            // rename_suggestions は path 主キーなので移行対象外（path が変わる）
            // classification_suggestions を移管
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO classification_suggestions(file_key, classified, confidence)
SELECT $new, classified, confidence FROM classification_suggestions WHERE file_key=$old
ON CONFLICT(file_key) DO UPDATE SET classified=excluded.classified, confidence=excluded.confidence;
DELETE FROM classification_suggestions WHERE file_key=$old;";
                cmd.Parameters.AddWithValue("$new", newFileKey);
                cmd.Parameters.AddWithValue("$old", oldFileKey);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }

        // ======= ★ BlockH 用 追加メソッド =======

        /// <summary>scopePath（null=全体, 文字列=親パスのLIKE前方一致）で DB の files を列挙</summary>
        public async IAsyncEnumerable<DbFileRecord> EnumerateFilesAsync(string? scopePath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            if (string.IsNullOrWhiteSpace(scopePath))
            {
                cmd.CommandText = "SELECT path,file_key,parent,name,ext,size,mtime,ctime,mime,summary,snippet,tags,classified,indexed_at FROM files";
            }
            else
            {
                cmd.CommandText = "SELECT path,file_key,parent,name,ext,size,mtime,ctime,mime,summary,snippet,tags,classified,indexed_at FROM files WHERE parent LIKE $p || '%'";
                cmd.Parameters.AddWithValue("$p", scopePath);
            }

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                yield return new DbFileRecord
                {
                    Path = r.GetString(0),
                    FileKey = r.IsDBNull(1) ? "" : r.GetString(1),
                    Parent = r.IsDBNull(2) ? null : r.GetString(2),
                    Name = r.IsDBNull(3) ? null : r.GetString(3),
                    Ext = r.IsDBNull(4) ? null : r.GetString(4),
                    Size = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    MTimeUnix = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                    CTimeUnix = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    Mime = r.IsDBNull(8) ? null : r.GetString(8),
                    Summary = r.IsDBNull(9) ? null : r.GetString(9),
                    Snippet = r.IsDBNull(10) ? null : r.GetString(10),
                    Tags = r.IsDBNull(11) ? null : r.GetString(11),
                    Classified = r.IsDBNull(12) ? null : r.GetString(12),
                    IndexedAt = r.IsDBNull(13) ? 0 : r.GetInt64(13)
                };
            }
        }

        /// <summary>path が DB に存在するか</summary>
        public async Task<bool> ExistsByPathAsync(string path, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM files WHERE path=$p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);
            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj != null;
        }

        /// <summary>path から 1 行を取得（なければ null）</summary>
        public async Task<DbFileRecord?> TryGetByPathAsync(string path, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path,file_key,parent,name,ext,size,mtime,ctime,mime,summary,snippet,tags,classified,indexed_at FROM files WHERE path=$p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            return new DbFileRecord
            {
                Path = r.GetString(0),
                FileKey = r.IsDBNull(1) ? "" : r.GetString(1),
                Parent = r.IsDBNull(2) ? null : r.GetString(2),
                Name = r.IsDBNull(3) ? null : r.GetString(3),
                Ext = r.IsDBNull(4) ? null : r.GetString(4),
                Size = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                MTimeUnix = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                CTimeUnix = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                Mime = r.IsDBNull(8) ? null : r.GetString(8),
                Summary = r.IsDBNull(9) ? null : r.GetString(9),
                Snippet = r.IsDBNull(10) ? null : r.GetString(10),
                Tags = r.IsDBNull(11) ? null : r.GetString(11),
                Classified = r.IsDBNull(12) ? null : r.GetString(12),
                IndexedAt = r.IsDBNull(13) ? 0 : r.GetInt64(13)
            };
        }

        /// <summary>指定 path の行を削除（ファイル自体は削除しない）</summary>
        public async Task DeleteByPathAsync(string path, CancellationToken ct = default)
        {
            await EnsureCreatedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Optional helpers
        public async Task<long> CountAsync(string table, CancellationToken ct = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj is long l ? l : Convert.ToInt64(obj);
        }
    }

    /// <summary>POCO for files table</summary>
    public sealed class DbFileRecord
    {
        public required string FileKey { get; init; }
        public required string Path { get; init; }
        public string? Parent { get; init; }
        public string? Name { get; init; }
        public string? Ext { get; init; }
        public long Size { get; init; }
        public long MTimeUnix { get; init; }
        public long CTimeUnix { get; init; }
        public string? Mime { get; init; }
        public string? Summary { get; init; }
        public string? Snippet { get; init; }
        public string? Tags { get; init; }       // ★追加：JSON配列文字列
        public string? Classified { get; init; }
        public long IndexedAt { get; init; }     // ★BlockH 用
    }

    public static class FileKeyUtil
    {
        /// <summary>Stable key from full path: SHA1(lowercase full path, UTF8)</summary>
        public static string GetStableKey(string fullPath)
        {
            using var sha1 = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant());
            var hash = sha1.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
