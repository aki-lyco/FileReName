using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Build
{
    /// <summary>
    /// 画像も含めて（ある場合は inline_data で）Gemini に分類を依頼する IAiClassifier 実装。
    /// </summary>
    public sealed class GeminiClassifier : IAiClassifier
    {
        private readonly HttpClient _http = new();
        private readonly string _apiKey;
        private readonly string _model; // 例: "gemini-1.5-flash" or "models/gemini-1.5-flash"

        public GeminiClassifier(string? apiKey = null, string? model = null)
        {
            // 優先順: 引数 > 環境変数 > 既定（空/既定モデル）
            _apiKey = apiKey
                   ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                   ?? "";

            _model = string.IsNullOrWhiteSpace(model)
                ? (Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-1.5-flash")
                : model!;
        }

        private string NormalizeModelForPath()
        {
            // API のパスは /v1beta/models/{model}:... なので、{model} は "gemini-..." 形式に正規化
            return _model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? _model.Substring("models/".Length)
                : _model;
        }

        public async Task<AiClassifyResult> ClassifyAsync(AiClassifyRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                // APIキーが無い環境では未分類に落とす
                return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed(no-api-key)");
            }

            var systemPrompt = BuildSystemPrompt(req);
            var userBlock = BuildUserBlock(req);

            // 画像ならパスとEXIFヒントを用意
            string? imagePath = null;
            string? imageHint = null;
            try
            {
                if (Explore.ImageInfoExtractor.LooksLikeImage(req.File.FullPath))
                {
                    imagePath = req.File.FullPath;
                    imageHint = await Explore.ImageInfoExtractor.ExtractSummaryAsync(req.File.FullPath);
                }
            }
            catch
            {
                // 画像の読み込みに失敗してもテキストのみで実行
                imagePath = null;
                imageHint = null;
            }

            // 3回リトライ（200/400/800ms）
            var delay = 200;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var json = await CallGeminiAsync(systemPrompt, userBlock, imagePath, imageHint, ct);
                    var result = Parse(json, req);
                    return result;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    System.Diagnostics.Debug.WriteLine($"GeminiClassifier retry {attempt}: {ex.Message}");
                    await Task.Delay(delay, ct);
                    delay *= 2;
                }
                catch
                {
                    // 失敗は未分類へ
                    return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed");
                }
            }

            return new AiClassifyResult(req.UncategorizedRelPath, 0, "", Array.Empty<string>(), "ai-failed");
        }

        private static string BuildSystemPrompt(AiClassifyRequest req)
        {
            return
@"あなたは “ファイル自動分類エンジン” です。与えられたカテゴリ一覧から最適な分類先を1つだけ選びます。
出力は JSON オブジェクトのみ。キー: path, confidence(0..1), summary, tags(配列), reason。
- path は与えられた 'rel_path' から必ず1つ選ぶ（新規カテゴリを作らない）
- 不確実（0.55未満）なら UNCATEGORIZED_REL_PATH を選び、reason を簡潔に
- summary は50文字以内、日本語で簡潔に
- tags は1〜5語、短い名詞（日本語可）
";
        }

        private static string BuildUserBlock(AiClassifyRequest req)
        {
            var catArr = req.Categories.Select(c =>
                $@"{{ ""rel_path"": ""{c.RelPath}"", ""display"": ""{c.Display}"", ""keywords"": {(c.Keywords == null ? "[]" : "[" + string.Join(",", c.Keywords.Select(k => $"\"{k}\"")) + "]")}, ""ext_filter"": {(string.IsNullOrWhiteSpace(c.ExtFilter) ? "null" : $"\"{c.ExtFilter}\"")}, ""ai_hint"": {(c.AiHint == null ? "null" : $@"""{c.AiHint}""")} }}").ToArray();
            var cats = string.Join(",", catArr);

            var text = req.ExtractedText ?? "";
            if (text.Length > 9000) text = text[..9000];

            var safeName = req.File.Name.Replace("\"", "\\\"");
            var safePath = req.File.FullPath.Replace("\"", "\\\"");
            var safeText = text.Replace("\"\"\"", "\"\\\"\\\"");

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($@"  ""UNCATEGORIZED_REL_PATH"": ""{req.UncategorizedRelPath}"",");
            sb.AppendLine($@"  ""CATEGORIES"": [ {cats} ],");
            sb.AppendLine(@"  ""FILE_META"": {");
            sb.AppendLine($@"    ""name"": ""{safeName}"", ""ext"": ""{req.File.Ext}"", ""full_path"": ""{safePath}"", ""mtime"": ""{req.File.Mtime:O}"", ""size_bytes"": {req.File.SizeBytes}");
            sb.AppendLine("  },");
            sb.AppendLine(@"  ""EXTRACTED_TEXT"":");
            sb.AppendLine($"\"\"\"{safeText}\"\"\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("上の情報と（あれば）添付画像を使って最適なカテゴリに振り分けて。出力はJSONのみ。");
            return sb.ToString();
        }

        private static string GetMimeFromExtension(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/*"
            };
        }

        private async Task<string> CallGeminiAsync(string system, string user, string? imagePath, string? imageHint, CancellationToken ct)
        {
            var modelForPath = NormalizeModelForPath();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelForPath}:generateContent?key={_apiKey}";

            // parts 構築（テキスト + 画像(任意) + ヒント(任意)）
            var parts = new System.Collections.Generic.List<object> { new { text = user } };

            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var mime = GetMimeFromExtension(imagePath);
                    var bytes = await File.ReadAllBytesAsync(imagePath, ct);
                    var b64 = Convert.ToBase64String(bytes);
                    parts.Add(new { inline_data = new { mime_type = mime, data = b64 } });
                    if (!string.IsNullOrWhiteSpace(imageHint))
                    {
                        parts.Add(new { text = $"[画像ヒント]\n{imageHint}" });
                    }
                }
                catch
                {
                    // 画像添付に失敗してもテキストだけで継続
                }
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            var payload = new
            {
                contents = new[] { new { role = "user", parts = parts.ToArray() } },
                systemInstruction = new { role = "system", parts = new[] { new { text = system } } },
                generationConfig = new { temperature = 0.2, maxOutputTokens = 512 }
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(); // ct 付きがない環境もあるため
            res.EnsureSuccessStatusCode();

            return json;
        }

        private static AiClassifyResult Parse(string responseJson, AiClassifyRequest req)
        {
            using var doc = JsonDocument.Parse(responseJson);

            // モデルの応答は {candidates[0].content.parts[0].text} に JSON文字列が入っている前提
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "";

            // 返ってきたテキスト(JSON文字列)をさらにJSONとして解釈
            using var inner = JsonDocument.Parse(text);
            var root = inner.RootElement;

            string path = root.GetProperty("path").GetString() ?? req.UncategorizedRelPath;
            double conf = 0.0;
            if (root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                conf = c.GetDouble();

            var sum = root.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
            string[] tags = Array.Empty<string>();
            if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            {
                tags = tg.EnumerateArray()
                         .Select(x => x.GetString() ?? "")
                         .Where(x => x.Length > 0)
                         .ToArray();
            }
            var rsn = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";

            // 低信頼は未分類へ
            if (conf < 0.55) path = req.UncategorizedRelPath;

            // 要約は最大50文字
            if (sum.Length > 50) sum = sum.Substring(0, 50);

            return new AiClassifyResult(path, conf, sum, tags, rsn);
        }
    }
}
