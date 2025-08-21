// Explore/Search/SearchService.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Explore.Search
{
    public sealed class SearchService
    {
        // ===== 公開モデル =====
        public sealed class SearchResult
        {
            public required QueryInfo Query { get; init; }
            public required List<SearchRow> Top5 { get; init; }
            public required List<SearchRow> Buffer20 { get; init; }
            public int NextOffset { get; init; }
        }

        public sealed class QueryInfo
        {
            public required string RawText { get; set; }
            public required string NormalizedJson { get; set; }
            public int Offset { get; set; }
        }

        public sealed class SearchRow
        {
            public long RowId { get; init; }
            public string Name { get; init; } = "";
            public string Ext { get; init; } = "";
            public string Path { get; init; } = "";
            public long Size { get; init; }
            public long MtimeUnix { get; init; }
            public long CtimeUnix { get; init; }
            public string Summary { get; init; } = "";
            public string Snippet { get; init; } = "";
            public double BaseScore { get; init; }
            public int TermScore { get; init; }
            public int Stage1Score { get; init; }   // must/ext/date/path の加点合計
        }

        // ===== 内部正規化モデル =====
        private sealed class Nq
        {
            public List<string> Terms { get; } = new();
            public List<string> Must { get; } = new();
            public List<string> ExtList { get; } = new();
            public List<string> PathLikes { get; } = new();
            public string Kind { get; set; } = "modified";
            public long? FromUnix { get; set; }
            public long? ToUnix { get; set; }
            public int Limit { get; set; } = 5;
            public int Offset { get; set; } = 0;
            public string RawText { get; set; } = "";
        }

        private readonly Func<string, CancellationToken, Task<string?>>? _normalizeAsync;
        private readonly string _dbPath;

        private bool _hasExt, _hasSize, _hasMtime, _hasCtime, _hasSummary, _hasSnippet;
        private string _idCol = "rowid";

        public Action<string>? DebugLog { get; set; }

        public SearchService(string dbPath, Func<string, CancellationToken, Task<string?>>? normalizeAsync)
        {
            _dbPath = dbPath;
            _normalizeAsync = normalizeAsync;
            LogInit();
        }

        // ===== ユーティリティ =====
        private SqliteConnection Open()
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Cache = SqliteCacheMode.Shared }.ToString();
            var c = new SqliteConnection(cs);
            c.Open();
            return c;
        }

        private void Debug(string message) => DebugLog?.Invoke(message);

        // ===== 診断 =====
        public async Task<(long files, long fts)> GetCountsAsync(CancellationToken ct)
        {
            await using var con = Open();
            long files = await ScalarAsync<long>(con, "SELECT COUNT(1) FROM files", ct);
            long fts = 0;
            try { fts = await ScalarAsync<long>(con, "SELECT COUNT(1) FROM files_fts", ct); } catch { }
            return (files, fts);
        }

        public void LogInit()
        {
            using var con = Open();
            _hasExt = HasColumn(con, "files", "ext");
            _hasSize = HasColumn(con, "files", "size");
            _hasMtime = HasColumn(con, "files", "mtime_unix");
            _hasCtime = HasColumn(con, "files", "ctime_unix");
            _hasSummary = HasColumn(con, "files", "summary");
            _hasSnippet = HasColumn(con, "files", "snippet");
            _idCol = "rowid";
            Debug($"schema: ext={_hasExt}, size={_hasSize}, mtime={_hasMtime}, ctime={_hasCtime}, summary={_hasSummary}, snippet={_hasSnippet}, idcol={_idCol}");
        }

        private static bool HasColumn(SqliteConnection con, string table, string col)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static async Task<T> ScalarAsync<T>(SqliteConnection con, string sql, CancellationToken ct = default)
        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            var obj = await cmd.ExecuteScalarAsync(ct);
            if (obj == null || obj is DBNull) return default!;
            return (T)Convert.ChangeType(obj, typeof(T), CultureInfo.InvariantCulture);
        }

        // ===== FTS 再構築 =====
        public async Task RebuildFtsAsync(CancellationToken ct)
        {
            await using var con = Open();
            string selSummary = _hasSummary ? "summary" : "''";
            string selSnippet = _hasSnippet ? "snippet" : "''";

            await using var cmd = con.CreateCommand();
            cmd.CommandText =
$@"
BEGIN;
DROP TABLE IF EXISTS files_fts;
CREATE VIRTUAL TABLE files_fts USING fts5(
    name, ext, path, summary, snippet,
    tokenize = 'unicode61'
);
INSERT INTO files_fts(rowid, name, ext, path, summary, snippet)
SELECT {_idCol}, name, {(_hasExt ? "ext" : "''")}, path, {selSummary}, {selSnippet} FROM files;
COMMIT;";
            await cmd.ExecuteNonQueryAsync(ct);
            Debug($"[fts] rebuilt (rowid <= files.{_idCol})");
        }

        // ===== メイン検索 =====
        public async Task<SearchResult> SearchAsync(string rawText, int offset, CancellationToken ct)
        {
            Debug($"[INPUT] {rawText}");
            var qInfo = new QueryInfo { RawText = rawText, NormalizedJson = "", Offset = offset };

            // 1) 正規化（3回リトライ、失敗時はフォールバック）
            string? json = null;
            if (_normalizeAsync != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    try { json = await _normalizeAsync(rawText, ct); } catch { }
                    if (IsJson(json)) break;
                    await Task.Delay(i == 0 ? 200 : i == 1 ? 400 : 800, ct);
                }
            }
            if (!IsJson(json)) json = BuildFallbackNormalizeJson(rawText);

            qInfo.NormalizedJson = json!;
            Debug($"[NLP] normalized: {json}");

            // 2) post（軽いクリップのみ）
            var nq = ParseAndPostProcess(json!);
            nq.RawText = rawText;
            Debug($"[POST] terms=[{string.Join(",", nq.Terms)}] , must=[{string.Join(",", nq.Must)}] , ext=[{string.Join(",", nq.ExtList)}] , pathLikes=[{string.Join(",", nq.PathLikes)}]  date={nq.Kind}/{nq.FromUnix?.ToString() ?? "null"}-{nq.ToUnix?.ToString() ?? "null"}");

            // 3) パイプライン
            using var con = Open();
            long totalFiles = await ScalarAsync<long>(con, "SELECT COUNT(1) FROM files", ct);
            int seedLimit = totalFiles > 200 ? 200 : int.MaxValue;

            // seed：CJK を含むなら LIKE に切替。英数字系は FTS→0ならLIKE
            List<(long rowid, double baseScore, string snippet)> seed;
            if (QueryLooksCjk(nq))
            {
                Debug("[seed] skip FTS (CJK detected) → LIKE");
                seed = await SeedFallbackLikeAsync(con, nq, 300, ct);
            }
            else
            {
                seed = await SeedAsync(con, nq, seedLimit, ct);
                Debug($"[seed] FTS candidates = {seed.Count}");
                if (seed.Count == 0)
                {
                    seed = await SeedFallbackLikeAsync(con, nq, 300, ct);
                    Debug($"[seed-fallback] LIKE candidates = {seed.Count}");
                }
            }

            // 1) Stage1：must/ext/date/path の“加点式”で上位100件
            var hits1 = await ScoreTop100Async(con, nq, seed, ct);
            Debug($"[stage1] top100 by s1_score/base/mtime = {hits1.Count}");

            // 2) term スコア → 上位20（Stage1Score を持ち越す）
            var hits2 = await TermScoreTop20Async(con, nq, hits1, ct);
            Debug($"[stage2] top20 by term_score + carry s1 = {hits2.Count}");

            // 3) final：summary/snippet を加点、Stage1+Term+SS の合計で上位5件
            var final5 = FinalTop5ByScoring(nq, hits2);
            Debug($"[final-scored] selected = {final5.Count}");

            // 不足時は hits2 から補充
            if (final5.Count < 5)
            {
                foreach (var h in hits2)
                {
                    if (final5.Any(x => x.RowId == h.RowId)) continue;
                    final5.Add(h);
                    if (final5.Count >= 5) break;
                }
            }

            return new SearchResult
            {
                Query = qInfo,
                Top5 = final5.Take(5).ToList(),
                Buffer20 = hits2,
                NextOffset = nq.Offset + 5
            };
        }

        // ====== seed/must/term/final ======
        private static bool IsJson(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s.StartsWith("{") && s.Contains("\"terms\"");
        }

        private static string BuildFallbackNormalizeJson(string raw)
        {
            raw ??= "";
            // JSON 文字列として必要最小限のエスケープ
            var esc = raw.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"{{\"terms\":[\"{esc}\"],\"mustPhrases\":[],\"extList\":[],\"locationHints\":[],\"pathLikes\":[],\"dateRange\":{{\"kind\":\"modified\",\"from\":null,\"to\":null}},\"limit\":5,\"offset\":0}}";
        }

        private static string EscapeLike(string s)
            => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        private static async Task<T> ScalarAsync<T>(SqliteConnection con, string sql) => await ScalarAsync<T>(con, sql, default);
        private static string EscapeFts(string s) => s.Replace("\"", "\"\"");
        private static bool ContainsCjk(string s) =>
            s.Any(ch =>
                (ch >= 0x3040 && ch <= 0x30FF) || (ch >= 0x3400 && ch <= 0x9FFF) || (ch >= 0xF900 && ch <= 0xFAFF));
        private static bool QueryLooksCjk(Nq q)
            => q.Terms.Any(ContainsCjk) || q.Must.Any(ContainsCjk);

        // 0) seed (FTS)
        private async Task<List<(long rowid, double baseScore, string snippet)>> SeedAsync(SqliteConnection con, Nq q, int limit, CancellationToken ct)
        {
            try
            {
                string broad = BuildBroadMatch(q.Terms, q.Must);
                var sql = $@"
WITH seed AS (
  SELECT rowid,
         bm25(files_fts, 3.0, 0.5, 0.2, 1.0, 0.8) AS base_score,
         snippet
    FROM files_fts
   WHERE files_fts MATCH $broad
   ORDER BY base_score
   {(limit == int.MaxValue ? "" : "LIMIT " + limit)}
)
SELECT rowid, base_score, snippet FROM seed;";
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$broad", broad);
                var list = new List<(long, double, string)>();
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add((r.GetInt64(0), r.GetDouble(1), r.IsDBNull(2) ? "" : r.GetString(2)));
                Debug($"[seed.sql] MATCH {broad}");
                return list;
            }
            catch { return new(); }
        }

        private static string BuildBroadMatch(List<string> terms, List<string> must)
        {
            var parts = new List<string>();
            parts.AddRange(terms.Select(t => $"{EscapeFts(t)}*"));
            parts.AddRange(must.Select(m => $"\"{EscapeFts(m)}\""));
            return string.Join(" OR ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        // 0’) seed fallback (LIKE)
        private async Task<List<(long rowid, double baseScore, string snippet)>> SeedFallbackLikeAsync(SqliteConnection con, Nq q, int limit, CancellationToken ct)
        {
            if (q.Terms.Count == 0) return new();

            var wh = new List<string>();
            var ps = new List<SqliteParameter>();
            int i = 0;
            foreach (var t in q.Terms)
            {
                var p = "%" + EscapeLike(t) + "%";
                wh.Add($"(f.name LIKE $t{i} ESCAPE '\\' OR f.path LIKE $t{i} ESCAPE '\\')");
                ps.Add(new SqliteParameter($"$t{i}", p));
                i++;
            }

            string selSnippet = _hasSnippet ? "COALESCE(f.snippet,'')" : "''";
            string orderKey = _hasMtime ? "f.mtime_unix" : $"f.{_idCol}";
            var sql = $@"
WITH seed AS (
  SELECT f.{_idCol} AS rowid,
         9999.0 AS base_score,
         {selSnippet} AS snippet
    FROM files f
   WHERE {(string.Join(" OR ", wh))}
   ORDER BY {orderKey} DESC
   LIMIT {limit}
)
SELECT rowid, base_score, snippet FROM seed;";
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var p in ps) cmd.Parameters.Add(p);

            var list = new List<(long, double, string)>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetInt64(0), r.GetDouble(1), r.IsDBNull(2) ? "" : r.GetString(2)));
            return list;
        }

        // 1) Stage1：must/ext/date/path の“加点式”で上位100
        private async Task<List<SearchRow>> ScoreTop100Async(SqliteConnection con, Nq q, List<(long rowid, double baseScore, string snippet)> seed, CancellationToken ct)
        {
            if (seed.Count == 0) return new();

            var ps = new List<SqliteParameter>();

            // 必須句（must）: 命中ごとに +3
            var s1 = new StringBuilder("0");
            for (int i = 0; i < q.Must.Count; i++)
            {
                string p = "%" + EscapeLike(q.Must[i].ToLowerInvariant()) + "%";
                s1.Append($" + (CASE WHEN (LOWER(f.name) LIKE $m{i} ESCAPE '\\' OR LOWER(f.path) LIKE $m{i} ESCAPE '\\') THEN 3 ELSE 0 END)");
                ps.Add(new SqliteParameter($"$m{i}", p));
            }

            // 拡張子: マッチで +2
            if (q.ExtList.Count > 0 && _hasExt)
            {
                var inClause = string.Join(",", q.ExtList.Select((e, i) => $"$e{i}"));
                s1.Append($" + (CASE WHEN LOWER(TRIM(f.ext,'.')) IN ({inClause}) THEN 2 ELSE 0 END)");
                for (int i = 0; i < q.ExtList.Count; i++)
                    ps.Add(new SqliteParameter($"$e{i}", q.ExtList[i].ToLowerInvariant()));
            }

            // pathLikes: 命中ごとに +3
            for (int i = 0; i < q.PathLikes.Count; i++)
            {
                s1.Append($" + (CASE WHEN f.path LIKE $pl{i} ESCAPE '\\' THEN 3 ELSE 0 END)");
                ps.Add(new SqliteParameter($"$pl{i}", q.PathLikes[i]));
            }

            // 日付: 範囲内 +2 / 片側のみ +1
            string dateCol = q.Kind == "created" && _hasCtime ? "f.ctime_unix" : _hasMtime ? "f.mtime_unix" : $"f.{_idCol}";
            if (q.FromUnix.HasValue && q.ToUnix.HasValue)
            {
                s1.Append($" + (CASE WHEN ({dateCol} >= $from AND {dateCol} < $to) THEN 2 ELSE 0 END)");
                ps.Add(new SqliteParameter("$from", q.FromUnix.Value));
                ps.Add(new SqliteParameter("$to", q.ToUnix.Value));
            }
            else if (q.FromUnix.HasValue)
            {
                s1.Append($" + (CASE WHEN {dateCol} >= $from THEN 1 ELSE 0 END)");
                ps.Add(new SqliteParameter("$from", q.FromUnix.Value));
            }
            else if (q.ToUnix.HasValue)
            {
                s1.Append($" + (CASE WHEN {dateCol} < $to THEN 1 ELSE 0 END)");
                ps.Add(new SqliteParameter("$to", q.ToUnix.Value));
            }

            // base_score（FTSのbm25）: 小さいほど良いので軽いボーナス
            s1.Append(" + (CASE WHEN s.base_score < 5 THEN 2 WHEN s.base_score < 10 THEN 1 ELSE 0 END)");

            string selSize = _hasSize ? "COALESCE(f.size,0) AS size" : "0 AS size";
            string selMtime = _hasMtime ? "COALESCE(f.mtime_unix,0) AS mtime_unix" : "0 AS mtime_unix";
            string selCtime = _hasCtime ? "COALESCE(f.ctime_unix,0) AS ctime_unix" : "0 AS ctime_unix";
            string selSummary = _hasSummary ? "COALESCE(f.summary,'') AS summary" : "'' AS summary";
            string selSnippet = _hasSnippet ? "COALESCE(f.snippet,'') AS snippet" : "'' AS snippet";
            string selExt = _hasExt ? "COALESCE(f.ext,'') AS ext" : "'' AS ext";

            var values = string.Join(",", seed.Select(s => $"({s.rowid},{s.baseScore.ToString(CultureInfo.InvariantCulture)})"));

            var sql = $@"
WITH seed(rowid, base_score) AS (VALUES {values}),
hits AS (
  SELECT f.{_idCol} AS rowid,
         f.name, {selExt}, f.path,
         {selSize}, {selMtime}, {selCtime},
         {selSummary}, {selSnippet},
         s.base_score AS base_score,
         ({s1}) AS stage1_score
    FROM seed s
    JOIN files f ON s.rowid = f.{_idCol}
   ORDER BY stage1_score DESC, s.base_score ASC, {dateCol} DESC
   LIMIT 100
)
SELECT * FROM hits;";
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var p in ps) cmd.Parameters.Add(p);

            var list = new List<SearchRow>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new SearchRow
                {
                    RowId = r.GetInt64(0),
                    Name = r.GetString(1),
                    Ext = r.GetString(2),
                    Path = r.GetString(3),
                    Size = r.GetInt64(4),
                    MtimeUnix = r.GetInt64(5),
                    CtimeUnix = r.GetInt64(6),
                    Summary = r.GetString(7),
                    Snippet = r.GetString(8),
                    BaseScore = r.GetDouble(9),
                    Stage1Score = r.GetInt32(10),
                    TermScore = 0
                });
            }
            Debug($"[stage1.sql] rows={list.Count} (order: s1 DESC, bm25 ASC, date DESC)");
            return list;
        }

        // 2) term score → 上位20（Stage1Score を持ち越す）
        private async Task<List<SearchRow>> TermScoreTop20Async(SqliteConnection con, Nq q, List<SearchRow> hits1, CancellationToken ct)
        {
            if (hits1.Count == 0) return new();

            var sbScore = new StringBuilder();
            if (q.Terms.Count == 0) sbScore.Append("0");
            else
            {
                for (int i = 0; i < q.Terms.Count; i++)
                {
                    if (i > 0) sbScore.Append(" + ");
                    sbScore.Append($"(CASE WHEN (LOWER(f.name) LIKE $t{i} OR LOWER(f.path) LIKE $t{i}) THEN 1 ELSE 0 END)");
                }
            }

            var values = string.Join(",", hits1.Select(h => $"({h.RowId},{h.BaseScore.ToString(CultureInfo.InvariantCulture)},{h.Stage1Score})"));
            string selSize = _hasSize ? "COALESCE(f.size,0) AS size" : "0 AS size";
            string selMtime = _hasMtime ? "COALESCE(f.mtime_unix,0) AS mtime_unix" : "0 AS mtime_unix";
            string selCtime = _hasCtime ? "COALESCE(f.ctime_unix,0) AS ctime_unix" : "0 AS ctime_unix";
            string selSummary = _hasSummary ? "COALESCE(f.summary,'') AS summary" : "'' AS summary";
            string selSnippet = _hasSnippet ? "COALESCE(f.snippet,'') AS snippet" : "'' AS snippet";
            string selExt = _hasExt ? "COALESCE(f.ext,'') AS ext" : "'' AS ext";
            string dateCol = _hasMtime ? "mtime_unix" : _hasCtime ? "ctime_unix" : "rowid";

            var sql = $@"
WITH h1(rowid, base_score, s1) AS (VALUES {values}),
hits AS (
  SELECT f.{_idCol} AS rowid,
         f.name, {selExt}, f.path,
         {selSize}, {selMtime}, {selCtime},
         {selSummary}, {selSnippet},
         ({sbScore}) AS term_score,
         h1.base_score AS base_score,
         h1.s1 AS stage1_score
    FROM h1
    JOIN files f ON h1.rowid = f.{_idCol}
   ORDER BY term_score DESC, base_score ASC, {dateCol} DESC
   LIMIT 20
)
SELECT * FROM hits;";
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < q.Terms.Count; i++)
                cmd.Parameters.AddWithValue($"$t{i}", "%" + q.Terms[i].ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");

            var list = new List<SearchRow>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new SearchRow
                {
                    RowId = r.GetInt64(0),
                    Name = r.GetString(1),
                    Ext = r.GetString(2),
                    Path = r.GetString(3),
                    Size = r.GetInt64(4),
                    MtimeUnix = r.GetInt64(5),
                    CtimeUnix = r.GetInt64(6),
                    Summary = r.GetString(7),
                    Snippet = r.GetString(8),
                    TermScore = r.GetInt32(9),
                    BaseScore = r.GetDouble(10),
                    Stage1Score = r.GetInt32(11)
                });
            }
            return list;
        }

        // 3) final：summary/snippet の出現回数を加点し、合計スコア上位5件
        private List<SearchRow> FinalTop5ByScoring(Nq q, List<SearchRow> hits2)
        {
            if (hits2.Count == 0) return new();

            static string L(string? s) => (s ?? string.Empty).ToLowerInvariant();

            var scored = new List<(SearchRow row, int ssScore, int totalScore)>(hits2.Count);

            foreach (var h in hits2)
            {
                int ss = 0;
                var sum = L(h.Summary);
                var sni = L(h.Snippet);

                foreach (var term in q.Terms)
                {
                    var t = term?.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    var tl = t.ToLowerInvariant();
                    ss += CountOccurrences(sum, tl);
                    ss += CountOccurrences(sni, tl);
                }

                int total = h.Stage1Score + h.TermScore + ss;
                scored.Add((h, ss, total));
            }

            var ordered = scored
                .OrderByDescending(x => x.totalScore)
                .ThenByDescending(x => x.row.TermScore)
                .ThenBy(x => x.row.BaseScore)
                .ThenByDescending(x => x.row.MtimeUnix)
                .Select(x => x.row)
                .Take(5)
                .ToList();

            return ordered;
        }

        private static int CountOccurrences(string haystackLower, string needleLower)
        {
            if (string.IsNullOrEmpty(haystackLower) || string.IsNullOrEmpty(needleLower)) return 0;
            int count = 0, idx = 0;
            while (true)
            {
                idx = haystackLower.IndexOf(needleLower, idx, StringComparison.Ordinal);
                if (idx < 0) break;
                count++;
                idx += needleLower.Length;
            }
            return count;
        }

        // ===== 正規化 post（軽いクリップのみ）=====
        private Nq ParseAndPostProcess(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var nq = new Nq();

            if (root.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
                foreach (var e in terms.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        nq.Terms.Add(e.GetString()!.Trim());

            if (root.TryGetProperty("mustPhrases", out var must) && must.ValueKind == JsonValueKind.Array)
                foreach (var e in must.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        nq.Must.Add(e.GetString()!.Trim());

            if (root.TryGetProperty("extList", out var exts) && exts.ValueKind == JsonValueKind.Array)
                foreach (var e in exts.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        nq.ExtList.Add(e.GetString()!.Trim().ToLowerInvariant());

            if (root.TryGetProperty("pathLikes", out var pls) && pls.ValueKind == JsonValueKind.Array)
                foreach (var e in pls.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        nq.PathLikes.Add(e.GetString()!.Trim());

            if (root.TryGetProperty("dateRange", out var dr) && dr.ValueKind == JsonValueKind.Object)
            {
                if (dr.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                    nq.Kind = k.GetString()!.Trim().ToLowerInvariant();

                long? toUnix = null, fromUnix = null;
                if (dr.TryGetProperty("from", out var f) && f.ValueKind != JsonValueKind.Null)
                {
                    if (f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var fv)) fromUnix = fv;
                    else if (f.ValueKind == JsonValueKind.String && DateTime.TryParse(f.GetString(), out var fdt)) fromUnix = new DateTimeOffset(fdt).ToUnixTimeSeconds();
                }
                if (dr.TryGetProperty("to", out var t) && t.ValueKind != JsonValueKind.Null)
                {
                    if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tv)) toUnix = tv;
                    else if (t.ValueKind == JsonValueKind.String && DateTime.TryParse(t.GetString(), out var tdt)) toUnix = new DateTimeOffset(tdt).ToUnixTimeSeconds();
                }
                nq.FromUnix = fromUnix;
                nq.ToUnix = toUnix;
            }

            if (root.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var lv)) nq.Limit = lv;
            if (root.TryGetProperty("offset", out var off) && off.TryGetInt32(out var ov)) nq.Offset = ov;

            if (nq.Terms.Count == 0)
            {
                var s = (doc.RootElement.ToString() ?? "").Trim();
                if (!string.IsNullOrEmpty(s)) nq.Terms.Add(s);
                else nq.Terms.Add("検索");
            }

            var allow = new HashSet<string>(new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "jpg", "jpeg", "png", "gif", "webp", "txt", "md", "csv" });
            nq.ExtList.RemoveAll(e => !allow.Contains(e));

            return nq;
        }
    }
}
