using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Build
{
    /// <summary>
    /// 画像（あれば inline_data）も同梱して Gemini に分類を依頼する IAiClassifier 実装。
    /// モデルの返答（JSON文字列）をログに残し、コードフェンス付きでも確実にパースする。
    /// </summary>
    public sealed class GeminiClassifier : IAiClassifier
    {
        private readonly HttpClient _http = new();
        private readonly string _apiKey;
        private readonly string _model; // 例: "gemini-1.5-flash"

        public GeminiClassifier(string? apiKey = null, string? model = null)
        {
            _apiKey = apiKey
                   ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                   ?? "";
            _model = string.IsNullOrWhiteSpace(model)
                ? (Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-1.5-flash")
                : model!;
        }

        private string NormalizeModelForPath()
        {
            return _model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? _model.Substring("models/".Length)
                : _model;
        }

        public async Task<AiClassifyResult> ClassifyAsync(AiClassifyRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed(no-api-key)");

            var systemPrompt = BuildSystemPrompt();
            var userBlock = BuildUserBlock(req);

            // ログ: 送信準備
            AppendGeminiLog("prompt",
                $"file={req.File.FullPath}\ninline={(req.InlineImage != null ? "yes" : "no")}, bytes={(req.InlineImage?.Length ?? 0)}, mime={req.ImageMime}\n--- USER BLOCK ---\n{Truncate(userBlock, 5000)}");

            // 3回リトライ
            var delay = 200;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var json = await CallGeminiAsync(systemPrompt, userBlock, req, ct);

                    // 候補テキスト（モデルが返したJSON文字列）を取り出してログ
                    var candidateText = ExtractCandidateText(json);
                    AppendGeminiLog("response_text", Truncate(candidateText, 8000));

                    // 解析（コードフェンスを剥がしてからJSONを読む）
                    var parsed = Parse(json, req);

                    // ログ: 解析結果（補正前の raw_path も復元してログ）
                    try
                    {
                        var cleaned = CleanCandidateJson(candidateText);
                        using var inner = JsonDocument.Parse(cleaned);
                        var rawPath = inner.RootElement.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
                        var rawConf = inner.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : double.NaN;
                        AppendGeminiLog("parsed",
                            $"raw_path={rawPath} raw_conf={rawConf:F3} => chosen_path={parsed.ClassifiedRelPath} conf={parsed.Confidence:F3}");
                    }
                    catch
                    {
                        AppendGeminiLog("parsed", $"(failed to parse candidate json text)");
                    }

                    return parsed;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    AppendGeminiLog("retry", $"attempt={attempt} ex={ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(delay, ct);
                    delay *= 2;
                }
                catch (Exception ex)
                {
                    AppendGeminiLog("error", ex.ToString());
                    return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed");
                }
            }

            return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed");
        }

        private static string BuildSystemPrompt()
        {
            return
@"あなたは “ファイル自動分類エンジン” です。与えられたカテゴリ一覧から最適な分類先を1つだけ選びます。
出力は JSON オブジェクトのみ。キー: path, confidence(0..1), summary, tags(配列), reason。
- path は与えられた 'rel_path' から必ず1つ選ぶ（新規カテゴリは作らない）
- 不確実（0.55未満）なら UNCATEGORIZED_REL_PATH を選び、reason を簡潔に
- summary は50文字以内（日本語）
- tags は1〜5語（名詞ベース）
";
        }

        private static string BuildUserBlock(AiClassifyRequest req)
        {
            var cats = string.Join(",", req.Categories.Select(c =>
                $@"{{ ""rel_path"": ""{c.RelPath}"", ""display"": ""{c.Display}"",
                     ""keywords"": {(c.Keywords == null ? "[]" : "[" + string.Join(",", c.Keywords.Select(k => $"\"{k}\"")) + "]")},
                     ""ext_filter"": {(string.IsNullOrWhiteSpace(c.ExtFilter) ? "null" : $"\"{c.ExtFilter}\"")},
                     ""ai_hint"": {(c.AiHint == null ? "null" : $@"""{c.AiHint}""")} }}"));

            var text = req.ExtractedText ?? "";
            if (text.Length > 9000) text = text[..9000];

            string J(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($@"  ""UNCATEGORIZED_REL_PATH"": ""{req.UncategorizedRelPath}"",");
            sb.AppendLine($@"  ""CATEGORIES"": [ {cats} ],");
            sb.AppendLine(@"  ""FILE_META"": {");
            sb.AppendLine($@"    ""name"": ""{J(req.File.Name)}"", ""ext"": ""{req.File.Ext}"", ""full_path"": ""{J(req.File.FullPath)}"", ""mtime"": ""{req.File.Mtime:O}"", ""size_bytes"": {req.File.SizeBytes}");
            sb.AppendLine("  },");
            sb.AppendLine(@"  ""EXTRACTED_TEXT"":");
            sb.AppendLine($"\"\"\"{text.Replace("\"\"\"", "\"\\\"\\\"")}\"\"\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("上の情報と（添付されていれば）画像を使って最適なカテゴリに振り分けて。出力はJSONのみ。");
            return sb.ToString();
        }

        private static string? MimeFromPath(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                ".heic" => "image/heic",
                ".heif" => "image/heif",
                ".avif" => "image/avif",
                _ => null
            };
        }

        private async Task<string> CallGeminiAsync(string system, string user, AiClassifyRequest req, CancellationToken ct)
        {
            var modelForPath = NormalizeModelForPath();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelForPath}:generateContent?key={_apiKey}";

            // parts 構築（InlineImage 最優先）
            var parts = new List<object> { new { text = user } };

            if (req.InlineImage is not null && !string.IsNullOrWhiteSpace(req.ImageMime))
            {
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = req.ImageMime!,
                        data = Convert.ToBase64String(req.InlineImage)
                    }
                });
            }
            else
            {
                try
                {
                    var mime = MimeFromPath(req.File.FullPath);
                    if (mime != null && File.Exists(req.File.FullPath))
                    {
                        var bytes = await File.ReadAllBytesAsync(req.File.FullPath, ct);
                        parts.Add(new { inline_data = new { mime_type = mime, data = Convert.ToBase64String(bytes) } });
                    }
                }
                catch
                {
                    // 添付失敗でもテキストだけで継続
                }
            }

            using var http = new HttpRequestMessage(HttpMethod.Post, url);
            var payload = new
            {
                contents = new[] { new { role = "user", parts = parts.ToArray() } },
                systemInstruction = new { role = "system", parts = new[] { new { text = system } } },
                generationConfig = new { temperature = 0.2, maxOutputTokens = 512 }
            };

            http.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(http, ct);
            var json = await res.Content.ReadAsStringAsync();
            res.EnsureSuccessStatusCode();
            return json;
        }

        private static string ExtractCandidateText(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement
                          .GetProperty("candidates")[0]
                          .GetProperty("content")
                          .GetProperty("parts")[0]
                          .GetProperty("text").GetString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string CleanCandidateJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var t = text.Trim();

            // 1) ```json ... ``` または ``` ... ``` を剥がす
            if (t.StartsWith("```"))
            {
                // 先頭 ``` を飛ばして末尾の ``` までを抜き出す
                var idxFirst = t.IndexOf("```", StringComparison.Ordinal);
                var idxSecond = t.IndexOf("```", idxFirst + 3, StringComparison.Ordinal);
                if (idxSecond > idxFirst)
                {
                    var inner = t.Substring(idxFirst + 3, idxSecond - (idxFirst + 3)).Trim();
                    // 先頭に言語名(json等)があれば1行落として除去
                    if (inner.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    {
                        inner = inner.Substring(4).TrimStart('\r', '\n', ' ', '\t');
                    }
                    t = inner.Trim();
                }
            }

            // 2) なお JSON 以外の文字混入に備え、最初の '{' から最後の '}' までを抽出
            var first = t.IndexOf('{');
            var last = t.LastIndexOf('}');
            if (first >= 0 && last >= first)
            {
                return t.Substring(first, last - first + 1).Trim();
            }

            return t;
        }

        private static AiClassifyResult Parse(string responseJson, AiClassifyRequest req)
        {
            // モデルの候補テキストを取り出し、コードフェンス等を除去してからJSONとして読む
            var candidateText = ExtractCandidateText(responseJson);
            var cleaned = CleanCandidateJson(candidateText);

            using var inner = JsonDocument.Parse(cleaned);
            var root = inner.RootElement;

            string path = root.TryGetProperty("path", out var p) ? (p.GetString() ?? req.UncategorizedRelPath) : req.UncategorizedRelPath;
            path = path.Trim();

            double conf = 0.0;
            if (root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                conf = c.GetDouble();

            var sum = root.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
            string[] tags = Array.Empty<string>();
            if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            {
                tags = tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
            }
            var rsn = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";

            // 低信頼は未分類へ
            if (conf < 0.55 || string.IsNullOrWhiteSpace(path))
                path = req.UncategorizedRelPath;

            // 要約は最大50文字
            if (sum.Length > 50) sum = sum[..50];

            return new AiClassifyResult(path, conf, sum, tags, rsn);
        }

        // ===== ログ補助 =====

        private static void AppendGeminiLog(string phase, string content)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileReName", "logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"gemini_{DateTime.Now:yyyyMMdd}.log");
                var line = $"[{DateTime.Now:O}] {phase}\n{content}\n---\n";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + " ...[trunc]";
        }
    }
}
