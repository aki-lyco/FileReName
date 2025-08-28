using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Explore
{
    /// <summary>
    /// 外部アセット（Poppler, Tesseract, tessdata など）を
    /// %LOCALAPPDATA%\FileReName\external に取得・展開（または.exeならサイレント実行）するヘルパ。
    /// ・.zip / .whl : 解凍して subdir 配下に展開
    /// ・.exe        : /VERYSILENT /NORESTART でサイレントインストール（Inno Setup想定）
    /// さらに：実体パスを AppSettings に保存し、次回は O(1) のクイックチェックを可能にする。
    /// </summary>
    public static class ExternalAssets
    {
        private static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "FileReName", "external");

        public sealed class Asset
        {
            public string name { get; set; } = "";
            public string version { get; set; } = "";
            public string url { get; set; } = "";
            public string sha256 { get; set; } = "";     // 空なら検証スキップ
            public string subdir { get; set; } = "";     // 展開先サブフォルダ（例: "poppler" / "tesseract\\tessdata"）
        }

        public sealed class Manifest { public Asset[] assets { get; set; } = Array.Empty<Asset>(); }

        public static string GetAssetDir(string name, string version)
            => Path.Combine(Root, name, version);

        /// <summary>name の中で“最新（文字列ソートで最大）”のバージョンディレクトリを返す</summary>
        public static string? TryResolve(string name)
        {
            var baseDir = Path.Combine(Root, name);
            if (!Directory.Exists(baseDir)) return null;
            var dirs = Directory.GetDirectories(baseDir);
            if (dirs.Length == 0) return null;
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            return dirs[^1];
        }

        // =====================================================================
        // ここから追加：クイックチェック & パス解決・保存
        // =====================================================================

        /// <summary>
        /// AppSettings に保存済みのフルパスだけを File.Exists/Directory.Exists で確認する“超高速”チェック。
        /// 必要な 3exe + tessdata が揃っていれば true。
        /// </summary>
        public static bool QuickCheckFromSettings()
        {
            var s = AppSettings.Load().ExternalTools;

            bool ok =
                !string.IsNullOrEmpty(s.PdfToText) && File.Exists(s.PdfToText) &&
                !string.IsNullOrEmpty(s.PdfToPpm) && File.Exists(s.PdfToPpm) &&
                !string.IsNullOrEmpty(s.Tesseract) && File.Exists(s.Tesseract) &&
                (string.IsNullOrEmpty(s.TessdataDir) || Directory.Exists(s.TessdataDir));

            return ok;
        }

        /// <summary>
        /// パス確定値を AppSettings に保存（次回 QuickCheck で O(1) 判定にする）
        /// </summary>
        public static void SaveResolvedToolPaths(string pdftotext, string pdftoppm, string tesseract, string tessdataDir, string? version = null)
        {
            var settings = AppSettings.Load();
            settings.ExternalTools.PdfToText = pdftotext;
            settings.ExternalTools.PdfToPpm = pdftoppm;
            settings.ExternalTools.Tesseract = tesseract;
            settings.ExternalTools.TessdataDir = tessdataDir;
            settings.ExternalTools.LastVerifiedUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(version))
                settings.ExternalTools.AssetsVersion = version;
            settings.Save();
        }

        /// <summary>
        /// Program Files / ローカル external 配下などを探索して実体パスを確定→保存。
        /// （重い再帰探索は“初回だけ”。以後は QuickCheckFromSettings で即判定）
        /// </summary>
        public static async Task ResolveAndCacheToolPathsAsync(IProgress<string>? log = null)
        {
            if (QuickCheckFromSettings())
            {
                log?.Report("✔ 外部ツール: 設定キャッシュで利用可能");
                return;
            }

            // 1) まず Program Files などの既知インストール先を試す（Tesseract）
            string[] pfCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR")
            };

            string? tesseractExe = null;
            string? tessdataDir = null;
            foreach (var baseDir in pfCandidates.Where(Directory.Exists))
            {
                var exe = Path.Combine(baseDir, "tesseract.exe");
                var td1 = Path.Combine(baseDir, "tessdata");
                if (File.Exists(exe))
                {
                    tesseractExe = exe;
                    if (Directory.Exists(td1)) tessdataDir = td1;
                    break;
                }
            }

            // 2) external\poppler\{latest}\... から pdftotext / pdftoppm を探索
            string? pdftotext = null;
            string? pdftoppm = null;

            var popplerRoot = TryResolve("poppler");
            if (popplerRoot != null && Directory.Exists(popplerRoot))
            {
                try
                {
                    // 再帰は初回だけ。以降は保存パスのみで即判定。
                    pdftotext = Directory.EnumerateFiles(popplerRoot, "pdftotext.exe", SearchOption.AllDirectories).FirstOrDefault();
                    pdftoppm = Directory.EnumerateFiles(popplerRoot, "pdftoppm.exe", SearchOption.AllDirectories).FirstOrDefault();
                }
                catch { /* パス長超等はスキップ */ }
            }

            // 3) external\tesseract\{latest}\... 配下に tesseract.exe / tessdata がある場合も探索（zip/whl 展開ケース）
            if (tesseractExe == null)
            {
                var tesseractRoot = TryResolve("tesseract");
                if (tesseractRoot != null && Directory.Exists(tesseractRoot))
                {
                    try
                    {
                        tesseractExe = Directory.EnumerateFiles(tesseractRoot, "tesseract.exe", SearchOption.AllDirectories).FirstOrDefault();
                        // tessdata はディレクトリごと, 代表的なファイルの存在でチェック
                        tessdataDir = Directory.EnumerateDirectories(tesseractRoot, "tessdata", SearchOption.AllDirectories)
                                              .FirstOrDefault(d =>
                                                  File.Exists(Path.Combine(d, "eng.traineddata")) ||
                                                  File.Exists(Path.Combine(d, "jpn.traineddata")));
                    }
                    catch { /* スキップ */ }
                }
            }

            if (pdftotext != null && pdftoppm != null && tesseractExe != null)
            {
                // tessdata は“空でも許容”。見つかっていれば保存（OCRの質は上がる）
                SaveResolvedToolPaths(pdftotext, pdftoppm, tesseractExe, tessdataDir ?? "", null);
                log?.Report("✔ 外部ツール: パスを確定して設定に保存しました");
                return;
            }

            log?.Report("⚠ 外部ツール: 一部の実体が見つかりませんでした（初回の展開が未完了の可能性）");
            await Task.CompletedTask;
        }

        // =====================================================================
        // ここまで追加
        // =====================================================================

        public static async Task EnsureAllAsync(Stream manifestJson, IProgress<string>? log = null)
        {
            Directory.CreateDirectory(Root);

            var mf = await JsonSerializer.DeserializeAsync<Manifest>(manifestJson)
                     ?? new Manifest();

            using var http = new HttpClient();

            foreach (var a in mf.assets)
            {
                var destDir = GetAssetDir(a.name, a.version);
                var okMarker = Path.Combine(destDir, ".ok");
                if (File.Exists(okMarker)) { log?.Report($"✔ {a.name} {a.version}"); continue; }

                Directory.CreateDirectory(destDir);

                // 取得ファイル名を拡張子で分岐
                var isExe = a.url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var payloadPath = Path.Combine(destDir, isExe ? "payload.exe" : "payload.zip");

                log?.Report($"↓ {a.name} {a.version} を取得中…");
                using (var res = await http.GetAsync(a.url))
                {
                    res.EnsureSuccessStatusCode();
                    await using var fs = File.Create(payloadPath);
                    await res.Content.CopyToAsync(fs);
                }

                // SHA256 検証（指定があれば）
                if (!string.IsNullOrWhiteSpace(a.sha256))
                {
                    using var sha = SHA256.Create();
                    await using var pfs = File.OpenRead(payloadPath);
                    var hash = Convert.ToHexString(sha.ComputeHash(pfs)).ToLowerInvariant();
                    if (!hash.Equals(a.sha256.ToLowerInvariant(), StringComparison.Ordinal))
                        throw new InvalidOperationException($"{a.name} のSHA256が一致しません");
                }

                if (isExe)
                {
                    // サイレント実行（Inno Setup の一般的なスイッチ）
                    var psi = new ProcessStartInfo
                    {
                        FileName = payloadPath,
                        Arguments = "/VERYSILENT /NORESTART",
                        UseShellExecute = true,  // UAC 昇格プロンプトを許可
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi)!;
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException($"{a.name} のサイレントインストールに失敗しました（ExitCode={p.ExitCode}）");
                }
                else
                {
                    // ZIP/WHL 展開（a.subdir 配下に上書き展開）
                    var targetDir = string.IsNullOrWhiteSpace(a.subdir) ? destDir
                                   : Path.Combine(destDir, a.subdir);
                    Directory.CreateDirectory(targetDir);

                    using (var z = ZipFile.OpenRead(payloadPath))
                    {
                        foreach (var e in z.Entries)
                        {
                            if (string.IsNullOrEmpty(e.Name)) continue; // ディレクトリ
                            var outPath = Path.Combine(targetDir, e.FullName);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            e.ExtractToFile(outPath, overwrite: true);
                        }
                    }
                }

                File.WriteAllText(okMarker, DateTimeOffset.UtcNow.ToString("o"));
                try { File.Delete(payloadPath); } catch { /* temp 削除失敗は無視 */ }

                log?.Report($"✔ {a.name} {a.version} 準備完了");
            }
        }
    }
}
