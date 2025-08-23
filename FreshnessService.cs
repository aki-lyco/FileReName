using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Indexing
{
    public enum FreshState { UpToDate, Stale, Unindexed, Orphan, Indexing, Error }

    public sealed record UnindexedHint(string FullPath, string Name, string Ext, long Size, long MtimeUnix);

    public interface IFreshnessService
    {
        Task<FreshState> GetFreshStateByPathAsync(string path, CancellationToken ct);
        Task<double> CalcFreshnessPercentAsync(string? scopePath, CancellationToken ct); // 0.0 - 1.0
        IAsyncEnumerable<UnindexedHint> FindUnindexedAsync(string scopePath, CancellationToken ct);
        void InvalidateFreshnessCache(string? scopePath = null);
    }

    /// <summary>BlockH: 鮮度判定ロジック（軽量キャッシュ付き）</summary>
    public sealed class FreshnessService : IFreshnessService
    {
        private readonly IndexDatabase _db;
        private readonly ConcurrentDictionary<string, FreshState> _cache = new(StringComparer.OrdinalIgnoreCase);

        public FreshnessService(IndexDatabase db) => _db = db;

        public void InvalidateFreshnessCache(string? scopePath = null)
        {
            if (scopePath is null) { _cache.Clear(); return; }
            foreach (var k in _cache.Keys)
                if (k.StartsWith(scopePath, StringComparison.OrdinalIgnoreCase))
                    _cache.TryRemove(k, out _);
        }

        public async Task<FreshState> GetFreshStateByPathAsync(string path, CancellationToken ct)
        {
            if (_cache.TryGetValue(path, out var hit)) return hit;

            try
            {
                var inDb = await _db.TryGetByPathAsync(path, ct);
                var exists = File.Exists(path);

                if (inDb is null)
                {
                    // DBに無い → FSにあれば Unindexed / 無ければ Error（通常来ない）
                    var st = exists ? FreshState.Unindexed : FreshState.Error;
                    _cache[path] = st;
                    return st;
                }

                if (!exists)
                {
                    _cache[path] = FreshState.Orphan;
                    return FreshState.Orphan;
                }

                // FS情報を取得
                var fi = new FileInfo(path);
                var fsMtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                var sizeOk = fi.Length == inDb.Size;
                var timeOk = Math.Abs(fsMtime - inDb.MTimeUnix) <= 2; // ±2秒許容
                var indexedOk = inDb.IndexedAt >= inDb.MTimeUnix;

                var state = (sizeOk && timeOk && indexedOk) ? FreshState.UpToDate : FreshState.Stale;
                _cache[path] = state;
                return state;
            }
            catch (UnauthorizedAccessException) { return FreshState.Error; }
            catch (PathTooLongException) { return FreshState.Error; }
            catch (IOException) { return FreshState.Error; }
        }

        public async Task<double> CalcFreshnessPercentAsync(string? scopePath, CancellationToken ct)
        {
            long total = 0;
            long staleOrOrphan = 0;

            await foreach (var row in _db.EnumerateFilesAsync(scopePath, ct))
            {
                ct.ThrowIfCancellationRequested();
                total++;

                var st = await GetFreshStateByPathAsync(row.Path, ct);
                if (st == FreshState.Stale || st == FreshState.Orphan)
                    staleOrOrphan++;
            }

            // scope 直下の未インデックス（➕）の推定を少しだけ足してもよいが、まずはDB主導で
            if (total <= 0) return 1.0;
            return 1.0 - (double)staleOrOrphan / total;
        }

        public async IAsyncEnumerable<UnindexedHint> FindUnindexedAsync(string scopePath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = false, // 直下のみ（MVP）
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            foreach (var path in Directory.EnumerateFiles(scopePath, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!await _db.ExistsByPathAsync(path, ct))
                {
                    var fi = new FileInfo(path);
                    yield return new UnindexedHint(
                        fi.FullName, fi.Name, fi.Extension,
                        fi.Exists ? fi.Length : 0,
                        new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()
                    );
                }
            }
        }
    }
}
