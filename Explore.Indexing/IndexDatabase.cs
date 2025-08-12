// Indexing/IndexDatabase.cs
// SQLiteインデックスの作成・更新（大量件数向けのバルクUpsert対応）
// DB: %LOCALAPPDATA%\FileReName\index.db

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Explore.Indexing
{
    public sealed class IndexDatabase
    {
        private readonly string _dbPath;
        private readonly string _connString;
        public string DatabasePath => _dbPath;

        public IndexDatabase(string? dbPath = null)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileReName");
            Directory.CreateDirectory(baseDir);
            _dbPath = dbPath ?? Path.Combine(baseDir, "index.db");
            _connString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        }

        /// <summary>DB作成（存在しなければ）と基本スキーマの用意</summary>
        public async Task EnsureCreatedAsync()
        {
            await using var conn = new SqliteConnection(_connString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS files(
  file_key    TEXT PRIMARY KEY,
  path        TEXT UNIQUE,
  parent      TEXT,
  name        TEXT,
  ext         TEXT,
  size        INTEGER,
  mtime_unix  INTEGER,
  ctime_unix  INTEGER,
  mime        TEXT,
  summary     TEXT,
  snippet     TEXT,
  classified  TEXT,
  indexed_at  INTEGER
);

CREATE TABLE IF NOT EXISTS rename_suggestions(
  file_key    TEXT PRIMARY KEY,
  suggested   TEXT
);

CREATE TABLE IF NOT EXISTS moves(
  id       INTEGER PRIMARY KEY,
  file_key TEXT,
  old_path TEXT,
  new_path TEXT,
  op       TEXT,
  reason   TEXT,
  at       INTEGER
);

CREATE INDEX IF NOT EXISTS idx_files_parent ON files(parent);
CREATE INDEX IF NOT EXISTS idx_files_ext    ON files(ext);
CREATE INDEX IF NOT EXISTS idx_files_mtime  ON files(mtime_unix);
";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>files テーブルに単発UPSERT</summary>
        public async Task UpsertFileAsync(DbFileRecord r)
        {
            await using var conn = new SqliteConnection(_connString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO files(file_key, path, parent, name, ext, size, mtime_unix, ctime_unix, mime, summary, snippet, classified, indexed_at)
VALUES($k,$p,$parent,$name,$ext,$size,$mt,$ct,$mime,$sum,$snip,$cls,$at)
ON CONFLICT(file_key) DO UPDATE SET
  path       = excluded.path,
  parent     = excluded.parent,
  name       = excluded.name,
  ext        = excluded.ext,
  size       = excluded.size,
  mtime_unix = excluded.mtime_unix,
  ctime_unix = excluded.ctime_unix,
  mime       = COALESCE(excluded.mime, files.mime),
  summary    = COALESCE(excluded.summary, files.summary),
  snippet    = COALESCE(excluded.snippet, files.snippet),
  classified = COALESCE(excluded.classified, files.classified),
  indexed_at = excluded.indexed_at;";
            cmd.Parameters.AddWithValue("$k", r.FileKey);
            cmd.Parameters.AddWithValue("$p", r.Path);
            cmd.Parameters.AddWithValue("$parent", (object?)r.Parent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)r.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ext", (object?)r.Ext ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", r.Size);
            cmd.Parameters.AddWithValue("$mt", r.MTimeUnix);
            cmd.Parameters.AddWithValue("$ct", r.CTimeUnix);
            cmd.Parameters.AddWithValue("$mime", (object?)r.Mime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sum", (object?)r.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$snip", (object?)r.Snippet ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cls", (object?)r.Classified ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>分類だけ更新</summary>
        public async Task UpsertClassificationAsync(string fileKey, string? cls)
        {
            await using var conn = new SqliteConnection(_connString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET classified=$c WHERE file_key=$k";
            cmd.Parameters.AddWithValue("$c", (object?)cls ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", fileKey);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>リネーム提案のUPSERT</summary>
        public async Task UpsertRenameSuggestionAsync(string fileKey, string? suggestion)
        {
            await using var conn = new SqliteConnection(_connString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO rename_suggestions(file_key, suggested)
VALUES($k,$s)
ON CONFLICT(file_key) DO UPDATE SET suggested=excluded.suggested;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$s", (object?)suggestion ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // --------- 大量件数向け：バルクUpsert（分割コミット + PRAGMA + busy_timeout） ---------

        private static async Task TuneAsync(SqliteConnection conn, CancellationToken ct)
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA synchronous=NORMAL;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000; PRAGMA busy_timeout=5000;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        /// <summary>
        /// レコードをストリームで受け取り、バッチでUpsert。
        /// - records は IAsyncEnumerable で“逐次”供給（メモリ使用を抑制）
        /// - batchSize 毎にコミットしてトランザクションを小さく保つ
        /// </summary>
        public async Task<(long scanned, long inserted)> BulkUpsertAsync(
            IAsyncEnumerable<DbFileRecord> records,
            int batchSize,
            IProgress<(long scanned, long inserted)>? progress,
            CancellationToken ct)
        {
            long scanned = 0, insertedGuess = 0;

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            await conn.OpenAsync(ct);
            await TuneAsync(conn, ct);

            long before = await CountAsync(conn, "files", ct);

            const string SQL = @"
INSERT INTO files (file_key, path, parent, name, ext, size, mtime_unix, ctime_unix, mime, summary, snippet, classified, indexed_at)
VALUES ($key,$path,$parent,$name,$ext,$size,$mtime,$ctime,$mime,$summary,$snippet,$classified,$at)
ON CONFLICT(file_key) DO UPDATE SET
  path=excluded.path,
  parent=excluded.parent,
  name=excluded.name,
  ext=excluded.ext,
  size=excluded.size,
  mtime_unix=excluded.mtime_unix,
  ctime_unix=excluded.ctime_unix,
  mime=COALESCE(excluded.mime, files.mime),
  summary=COALESCE(excluded.summary, files.summary),
  snippet=COALESCE(excluded.snippet, files.snippet),
  classified=COALESCE(excluded.classified, files.classified),
  indexed_at=excluded.indexed_at;";

            // ★ SqliteTransaction で厳密に扱う（型不一致の解消）
            SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SQL;

                var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
                var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
                var pParent = cmd.CreateParameter(); pParent.ParameterName = "$parent"; cmd.Parameters.Add(pParent);
                var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
                var pExt = cmd.CreateParameter(); pExt.ParameterName = "$ext"; cmd.Parameters.Add(pExt);
                var pSize = cmd.CreateParameter(); pSize.ParameterName = "$size"; cmd.Parameters.Add(pSize);
                var pM = cmd.CreateParameter(); pM.ParameterName = "$mtime"; cmd.Parameters.Add(pM);
                var pC = cmd.CreateParameter(); pC.ParameterName = "$ctime"; cmd.Parameters.Add(pC);
                var pMime = cmd.CreateParameter(); pMime.ParameterName = "$mime"; cmd.Parameters.Add(pMime);
                var pSum = cmd.CreateParameter(); pSum.ParameterName = "$summary"; cmd.Parameters.Add(pSum);
                var pSnip = cmd.CreateParameter(); pSnip.ParameterName = "$snippet"; cmd.Parameters.Add(pSnip);
                var pCls = cmd.CreateParameter(); pCls.ParameterName = "$classified"; cmd.Parameters.Add(pCls);
                var pAt = cmd.CreateParameter(); pAt.ParameterName = "$at"; cmd.Parameters.Add(pAt);

                int inBatch = 0;

                await foreach (var r in records.WithCancellation(ct))
                {
                    scanned++;

                    pKey.Value = r.FileKey ?? (object)DBNull.Value;
                    pPath.Value = r.Path ?? (object)DBNull.Value;
                    pParent.Value = r.Parent ?? (object)DBNull.Value;
                    pName.Value = r.Name ?? (object)DBNull.Value;
                    pExt.Value = r.Ext ?? (object)DBNull.Value;
                    pSize.Value = r.Size;
                    pM.Value = r.MTimeUnix;
                    pC.Value = r.CTimeUnix;
                    pMime.Value = (object?)r.Mime ?? DBNull.Value;
                    pSum.Value = (object?)r.Summary ?? DBNull.Value;
                    pSnip.Value = (object?)r.Snippet ?? DBNull.Value;
                    pCls.Value = (object?)r.Classified ?? DBNull.Value;
                    pAt.Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    await cmd.ExecuteNonQueryAsync(ct);
                    inBatch++;

                    if (inBatch >= batchSize)
                    {
                        await tx.CommitAsync(ct);
                        progress?.Report((scanned, insertedGuess));
                        inBatch = 0;

                        await tx.DisposeAsync();
                        tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                        cmd.Transaction = tx; // 新しいTxに付け替え
                    }
                }

                await tx.CommitAsync(ct);
            }
            finally
            {
                await tx.DisposeAsync();
            }

            long after = await CountAsync(conn, "files", ct);
            insertedGuess = Math.Max(0, after - before);

            progress?.Report((scanned, insertedGuess));
            return (scanned, insertedGuess);
        }

        private static async Task<long> CountAsync(SqliteConnection conn, string table, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            var obj = await cmd.ExecuteScalarAsync(ct);
            return (long)(obj ?? 0L);
        }
    }

    /// <summary>filesテーブルの1行を表現する最小モデル</summary>
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

    /// <summary>
    /// 安定ファイルキーを作るユーティリティ。
    /// 1) Win32: VolumeSerialNumber + FileIndex
    /// 2) 失敗時は path の SHA-256
    /// </summary>
    public static class FileKeyUtil
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        public static string GetStableKey(string path)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (GetFileInformationByHandle(fs.SafeFileHandle, out var info))
                {
                    ulong fileIndex = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
                    return $"{info.dwVolumeSerialNumber:X}-{fileIndex:X}";
                }
            }
            catch
            {
                // ignore → フォールバックへ
            }

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(path);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
