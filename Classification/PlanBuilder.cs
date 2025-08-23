using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Explore.FileSystem;   // PathUtils
using Explore.Indexing;     // IndexDatabase, FileKeyUtil

namespace Explore.Build
{
    public static class PlanBuilder
    {
        private const double DefaultThreshold = 0.55;

        public static async Task<BuildPlan> MakePlanAsync(
            IReadOnlyList<string> selectedTargets,
            CategoryNode root,
            string classificationBasePath,
            CancellationToken ct,
            IAiClassifier? classifier = null,
            double? thresholdOverride = null)
        {
            double threshold = thresholdOverride ?? DefaultThreshold;
            classifier ??= new GeminiClassifier();

            // 未分類の相対パス
            string uncRel = NormalizeRel(root.Children.First(c => c.IsRequired).RelPath);

            // カテゴリ定義（Keywords は string[] 固定に）
            var categoryDefs = root.FlattenTree()
                                   .Where(n => !n.IsRequired && !string.IsNullOrWhiteSpace(n.RelPath))
                                   .Select(n => new CategoryDef(
                                       NormalizeRel(n.RelPath),
                                       n.Display,
                                       ParseKeywords(n.KeywordsJson),
                                       n.ExtFilter,
                                       n.AiHint))
                                   .ToList();

            // 対象ファイルを展開
            var files = ExpandTargets(selectedTargets).ToList();

            var db = new IndexDatabase();
            await db.EnsureCreatedAsync();

            var moves = new List<MoveItem>();
            var unresolved = new List<string>();

            foreach (var full in files)
            {
                ct.ThrowIfCancellationRequested();

                var fi = new FileInfo(full);
                var extNoDot = (fi.Extension ?? string.Empty).TrimStart('.');

                // FileMeta(Name, Ext, FullPath, Mtime, SizeBytes)
                var meta = new FileMeta(
                    fi.Name,
                    extNoDot,
                    fi.FullName,
                    new DateTimeOffset(fi.LastWriteTimeUtc),
                    fi.Exists ? fi.Length : 0);

                // テキスト抽出
                var raw = await TextExtractor.ExtractAsync(full);
                var text = TextExtractor.NormalizeAndTrim(raw);

                // ★本文が空なら、ファイル名＋親フォルダ名から疑似本文を作る（Popplerが無い/薄いPDFでも分類を安定化）
                if (string.IsNullOrWhiteSpace(text))
                    text = BuildFilenameContext(fi);

                // ★ログは「AIへ渡す最終テキスト」で記録（フォールバック後なので len>0 になるはず）
                AppendExtractLog(full, text);

                // AI分類
                var req = new AiClassifyRequest(classificationBasePath, uncRel, categoryDefs, meta, text);
                var result = await classifier.ClassifyAsync(req, ct);

                // しきい値で未分類にフォールバック
                string chosenRel = (result.Confidence < threshold || string.IsNullOrWhiteSpace(result.ClassifiedRelPath))
                    ? uncRel
                    : NormalizeRel(result.ClassifiedRelPath);

                // DB: 提案 + summary/snippet/tags を保存
                var key = FileKeyUtil.GetStableKey(full);
                await db.UpsertClassificationSuggestionAsync(key, chosenRel, result.Confidence);
                await db.UpdateSummarySnippetTagsAsync(
                    key,
                    result.Summary ?? string.Empty,
                    MakeSnippet(text),
                    (result.Tags != null && result.Tags.Length > 0) ? JsonSerializer.Serialize(result.Tags) : null,
                    ct
                );

                // 予定の移動先
                string destDirAbs = PathUtils.CombineBase(classificationBasePath, chosenRel);
                string destAbs = GetAvailableName(destDirAbs, fi.Name);

                if (!string.Equals(fi.FullName, destAbs, StringComparison.OrdinalIgnoreCase))
                {
                    moves.Add(new MoveItem(fi.FullName, destAbs, result.Confidence >= threshold ? "classified" : "fallback"));
                }

                if (chosenRel == uncRel)
                    unresolved.Add(fi.FullName);
            }

            // 作成すべきディレクトリ（Relに戻して CreateDir）
            var createDirs = moves
                .Select(m => Path.GetDirectoryName(m.DestFullPath)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(abs => new CreateDir(ToRelFromBase(classificationBasePath, abs)))
                .ToList();

            var stats = new BuildPlanStats(
                CreateCount: createDirs.Count,
                RenameCount: 0,
                MoveCount: moves.Count,
                UnresolvedCount: unresolved.Count,
                ErrorCount: 0
            );

            return new BuildPlan(
                CreateDirs: createDirs,
                RenameDirs: Array.Empty<RenameDir>(),
                Moves: moves,
                Unresolved: unresolved,
                Errors: Array.Empty<PlanError>(),
                Stats: stats
            );
        }

        // === helpers ===

        private static IEnumerable<string> ExpandTargets(IReadOnlyList<string> targets)
        {
            foreach (var t in targets ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(t)) continue;

                if (File.Exists(t))
                {
                    yield return Path.GetFullPath(t);
                }
                else if (Directory.Exists(t))
                {
                    IEnumerable<string> inner = Enumerable.Empty<string>();
                    try
                    {
                        inner = Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories);
                    }
                    catch
                    {
                        // 読めないフォルダはスキップ
                    }

                    foreach (var f in inner)
                        yield return Path.GetFullPath(f);
                }
            }
        }

        private static string NormalizeRel(string? rel)
        {
            rel ??= string.Empty;
            rel = rel.Replace('\\', '/').Trim('/');
            return rel;
        }

        // ★常に string[] を返す（空なら Array.Empty<string>()）
        private static string[] ParseKeywords(string? jsonOrCsv)
        {
            if (string.IsNullOrWhiteSpace(jsonOrCsv)) return Array.Empty<string>();
            var s = jsonOrCsv.Trim();

            if (s.StartsWith("["))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string?[]>(s) ?? Array.Empty<string?>();
                    var cleaned = arr.Where(a => !string.IsNullOrWhiteSpace(a))
                                     .Select(a => a!.Trim())
                                     .Where(a => a.Length > 0)
                                     .ToArray();
                    return cleaned.Length == 0 ? Array.Empty<string>() : cleaned;
                }
                catch
                {
                    // JSONで解釈できなければ CSV にフォールバック
                }
            }

            var parts = s.Split(new[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Trim())
                         .Where(x => x.Length > 0)
                         .ToArray();
            return parts.Length == 0 ? Array.Empty<string>() : parts;
        }

        private static string MakeSnippet(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        private static string GetAvailableName(string destDirAbs, string fileName)
        {
            Directory.CreateDirectory(destDirAbs);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var cand = Path.Combine(destDirAbs, fileName);
            if (!File.Exists(cand)) return cand;
            int i = 1;
            while (true)
            {
                cand = Path.Combine(destDirAbs, $"{name} ({i}){ext}");
                if (!File.Exists(cand)) return cand;
                i++;
            }
        }

        private static string ToRelFromBase(string baseDir, string absPath)
        {
            var baseFull = Path.GetFullPath(baseDir);
            var full = Path.GetFullPath(absPath);
            if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var rel = full.Substring(baseFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>
        /// 本文抽出が空のときに、AIへのヒントとして
        /// 「ファイル名（拡張子なし）＋親/祖父フォルダ名」をスペース結合で返す
        /// 例: 『R6_前期_日本史』 + 『日本史』 + 『2年_過去問』
        /// </summary>
        private static string BuildFilenameContext(FileInfo fi)
        {
            static string Clean(string s) => s.Replace('_', ' ')
                                             .Replace('-', ' ')
                                             .Replace('（', ' ')
                                             .Replace('）', ' ')
                                             .Trim();

            var parts = new List<string> { Clean(Path.GetFileNameWithoutExtension(fi.Name)) };
            var d = fi.Directory;
            for (int i = 0; i < 2 && d != null; i++)
            {
                parts.Add(Clean(d.Name));
                d = d.Parent;
            }
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        /// <summary>%LOCALAPPDATA%\FileReName\logs\extract_YYYYMMDD.log に抽出ログを追記</summary>
        private static void AppendExtractLog(string fullPath, string? normalizedText)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName", "logs");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, $"extract_{DateTime.Now:yyyyMMdd}.log");

                var len = string.IsNullOrEmpty(normalizedText) ? 0 : normalizedText.Length;
                var ok200 = len >= 200 ? "OK200" : "LT200";
                var head = normalizedText ?? string.Empty;
                if (head.Length > 200) head = head.Substring(0, 200);
                head = head.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

                var line = $"{DateTime.Now:O}\t{ok200}\t{len}\t{fullPath}\t{head}\n";
                File.AppendAllText(logPath, line, System.Text.Encoding.UTF8);
            }
            catch
            {
                // ログ失敗は無視
            }
        }
    }
}
