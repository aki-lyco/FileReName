using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
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

            // �����ނ̑��΃p�X
            string uncRel = NormalizeRel(root.Children.First(c => c.IsRequired).RelPath);

            // �J�e�S����`�i�����ވȊO�ARelPath����łȂ��m�[�h�̂݁j
            var categoryDefs = root.FlattenTree()
                                   .Where(n => !n.IsRequired && !string.IsNullOrWhiteSpace(n.RelPath))
                                   .Select(n => new CategoryDef(
                                       NormalizeRel(n.RelPath),
                                       n.Display,
                                       ParseKeywords(n.KeywordsJson),
                                       n.ExtFilter,
                                       n.AiHint))
                                   .ToList();

            // �Ώۃt�@�C����W�J
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

                var meta = new FileMeta(
                    fi.Name,
                    extNoDot,
                    fi.FullName,
                    new DateTimeOffset(fi.LastWriteTimeUtc),
                    fi.Exists ? fi.Length : 0);

                // ========== �e�L�X�g���o or �t�@�C�����R���e�L�X�g ==========
                string text = await TextExtractor.ExtractAsync(full);
                if (string.IsNullOrWhiteSpace(text))
                    text = BuildFilenameContext(fi);

                // ========== �摜�Ȃ� OCR + �摜��Y�t ==========
                byte[]? imgBytes = null;
                string? imgMime = null;
                if (LooksLikeImage(fi.Extension))
                {
                    var img = await TextExtractor.ExtractImageAsync(full);
                    if (!string.IsNullOrWhiteSpace(img.OcrText))
                        text = string.Join("\n", new[] { text, "[OCR]", img.OcrText }.Where(s => !string.IsNullOrWhiteSpace(s)));

                    imgBytes = img.ImageBytes;
                    imgMime = img.ImageMime;
                }

                AppendExtractLog(full, text);

                // ========== AI���� ==========
                var req = new AiClassifyRequest(
                    classificationBasePath,
                    uncRel,
                    categoryDefs,
                    meta,
                    TextExtractor.NormalizeAndTrim(text, 8 * 1024),
                    imgBytes,
                    imgMime
                );

                var result = await classifier.ClassifyAsync(req, ct);

                // �������l����
                string modelPath = (result.ClassifiedRelPath ?? "").Trim();
                string chosenRel = (result.Confidence < threshold || string.IsNullOrWhiteSpace(modelPath))
                    ? uncRel
                    : NormalizeRel(modelPath);

                // �� �ڍ׃��O�iPlan���j
                AppendClassifyLog(
                    fullPath: full,
                    modelPath: modelPath,
                    chosenRel: chosenRel,
                    confidence: result.Confidence,
                    imageBytes: imgBytes?.Length ?? 0
                );

                // DB: ��� + summary/snippet/tags ��ۑ�
                var key = FileKeyUtil.GetStableKey(full);
                await db.UpsertClassificationSuggestionAsync(key, chosenRel, result.Confidence);
                await db.UpdateSummarySnippetTagsAsync(
                    key,
                    result.Summary ?? string.Empty,
                    MakeSnippet(text),
                    (result.Tags != null && result.Tags.Length > 0) ? JsonSerializer.Serialize(result.Tags) : null,
                    ct
                );

                // �\��̈ړ���
                string destDirAbs = PathUtils.CombineBase(classificationBasePath, chosenRel);
                string destAbs = GetAvailableName(destDirAbs, fi.Name);

                var moveReason = chosenRel == uncRel ? "fallback" : "classified";
                if (!string.Equals(fi.FullName, destAbs, StringComparison.OrdinalIgnoreCase))
                {
                    moves.Add(new MoveItem(fi.FullName, destAbs, moveReason));
                }

                if (chosenRel == uncRel)
                    unresolved.Add(fi.FullName);
            }

            // �쐬���ׂ��f�B���N�g��
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

        private static bool LooksLikeImage(string? ext)
        {
            var e = (ext ?? "").ToLowerInvariant();
            return e is ".png" or ".jpg" or ".jpeg" or ".jfif"
                   or ".webp" or ".bmp" or ".gif"
                   or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif";
        }

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
                        // �ǂ߂Ȃ��t�H���_�̓X�L�b�v
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
                    // JSON �����Ă����ꍇ�� CSV �փt�H�[���o�b�N
                }
            }

            var parts = s.Split(new[] { ',', '�A', ';' }, StringSplitOptions.RemoveEmptyEntries)
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

        // ���h���C�����Ńf�B�X�N�������Ȃ����߁A�����ł̓t�H���_�����Ȃ�
        private static string GetAvailableName(string destDirAbs, string fileName)
        {
            // Directory.CreateDirectory(destDirAbs);  // ���폜�I

            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);

            // �ړI�f�B���N�g�������݂��Ȃ��ꍇ�ł��A�����́u�z�肳���ŏI�p�X�v��Ԃ�����
            // ���ۂɃt�H���_�����̂� PlanApplier.ApplyAsync ��
            var candidate = Path.Combine(destDirAbs, fileName);
            if (!File.Exists(candidate)) return candidate;

            int i = 1;
            while (true)
            {
                candidate = Path.Combine(destDirAbs, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
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
        /// �{�����o����̂Ƃ��̃q���g�i�t�@�C�����{�e/�c���t�H���_���j
        /// </summary>
        private static string BuildFilenameContext(FileInfo fi)
        {
            static string Clean(string s) => s.Replace('_', ' ')
                                             .Replace('-', ' ')
                                             .Replace('�i', ' ')
                                             .Replace('�j', ' ')
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

        /// <summary>%LOCALAPPDATA%\FileReName\logs\extract_YYYYMMDD.log</summary>
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
                if (head.Length > 200) head = head[..200];
                head = head.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

                var line = $"{DateTime.Now:O}\t{ok200}\t{len}\t{fullPath}\t{head}\n";
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        /// <summary>%LOCALAPPDATA%\FileReName\logs\classify_YYYYMMDD.log</summary>
        private static void AppendClassifyLog(string fullPath, string modelPath, string chosenRel, double confidence, int imageBytes)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName", "logs");
                Directory.CreateDirectory(dir);
                var log = Path.Combine(dir, $"classify_{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:O}\tmodelPath={modelPath}\tchosen={chosenRel}\tconf={confidence:F3}\timageBytes={imageBytes}\t{fullPath}\n";
                File.AppendAllText(log, line, Encoding.UTF8);
            }
            catch { /* ignore */ }
        }
    }
}
