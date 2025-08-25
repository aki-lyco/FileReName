using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
