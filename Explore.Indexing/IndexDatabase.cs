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
        // �A�v�����̓����������݂𒼗�
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
  classified  TEXT,
  indexed_at  INTEGER
);
CREATE INDEX IF NOT EXISTS idx_files_parent ON files(parent);
-- �݊��̂��ߕێ��iPRIMARY KEY �Əd�����Ă��Q�Ȃ��j
CREATE UNIQUE INDEX IF NOT EXISTS idx_files_path ON files(path);

CREATE TABLE IF NOT EXISTS rename_suggestions(
  path        TEXT PRIMARY KEY,
  suggestion  TEXT,
  updated_at  INTEGER
);
";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SQL;
            await cmd.ExecuteNonQueryAsync(ct);
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

            await s_writeGate.WaitAsync(ct); // �A�v��������
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
                    if (!seenInBatch.Add(r.Path)) continue; // �o�b�`���d�����Ԉ���

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
        /// 1�������S�� UPSERT�iINSERT OR IGNORE �� UPDATE�j�B��O�Ŏ~�߂Ȃ��B
        /// �� �����͓��� BeginTransaction/Commit ���g�p�iDbTransaction �^�s��v�G���[����j
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
(path, file_key, parent, name, ext, size, mtime, ctime, mime, summary, snippet, classified, indexed_at)
VALUES
($path, $key, $parent, $name, $ext, $size, $mtime, $ctime, $mime, $summary, $snippet, $classified, $at);";
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
            var i_classified = insert.CreateParameter(); i_classified.ParameterName = "$classified"; insert.Parameters.Add(i_classified);
            var i_at = insert.CreateParameter(); i_at.ParameterName = "$at"; insert.Parameters.Add(i_at);

            // UPDATE�i����/�V�K�ǂ���ɂ��K�p�j
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
            var u_classified = update.CreateParameter(); u_classified.ParameterName = "$classified"; update.Parameters.Add(u_classified);
            var u_at = update.CreateParameter(); u_at.ParameterName = "$at"; update.Parameters.Add(u_at);

            int scanned = 0, inserted = 0;

            foreach (var r in batch)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                // ���ʒl
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
                var classified = (object?)r.Classified ?? DBNull.Value;

                // INSERT OR IGNORE�i�V�K�̂� 1 ��Ԃ��j
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

                // �K�� UPDATE�i����/�V�K�Ɋւ�炸�ŐV�l�ŏ㏑���j
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
                u_classified.Value = i_classified.Value;
                u_at.Value = now;

                await update.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return (scanned, inserted);
        }

        // ===== �P�� UPSERT �w���p�[�iJsonImporter ������Ă΂��j =====

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
(path, file_key, parent, name, ext, size, mtime, ctime, mime, summary, snippet, classified, indexed_at)
VALUES
($path, $key, $parent, $name, $ext, $size, $mtime, $ctime, $mime, $summary, $snippet, $classified, $at)
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
        public string? Classified { get; init; }
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
