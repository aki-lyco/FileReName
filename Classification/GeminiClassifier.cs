using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Build
{
    public sealed class GeminiClassifier : IAiClassifier
    {
        private readonly HttpClient _http = new();
        private readonly string _apiKey;

        public GeminiClassifier(string? apiKey = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
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

            // 3回リトライ（200/400/800ms）
            var delay = 200;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var json = await CallGeminiAsync(systemPrompt, userBlock, ct);
                    var result = Parse(json, req);
                    return result;
                }
                catch when (attempt < 3)
                {
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
@"あなたは “ファイル自動分類エンジン” です。与えられたカテゴリ一覧から最適な分類先を選びます。
出力は JSON オブジェクトのみ（説明文・コードフェンス禁止）。

【タスク】
- 入力ファイル（name/ext/path/mtime/size）と抽出テキストを読み、
  提供されたカテゴリ（ベース直下の相対パス）から最適な classified_path を1つ選びなさい。
- 併せて 1) 最大50文字の要約 summary、2) 1〜5語の tags[] を生成。
- 確信度が低い場合は ""{{UNCATEGORIZED_REL_PATH}}"" を選ぶ。
- 新しいカテゴリを発明してはならない。必ず categories.rel_path から選ぶ。
- ext_filter があるカテゴリは拡張子不一致で confidence を下げる（完全排除はしない）。
- 根拠の優先度：name > text > ext > path。

【出力スキーマ（キー順厳守）】
{
  ""classified_path"": """",
  ""confidence"": 0.0,
  ""summary"": """",
  ""tags"": [],
  ""reason"": """"
}

【判定ガイド】
- name/本文に重要語が複数命中: 0.75〜0.95
- nameに1語 or 本文に1語 + ext一致: 0.60〜0.75
- 曖昧（拡張子/ヒント程度）: 0.50〜0.60 → 0.55未満は ""{{UNCATEGORIZED_REL_PATH}}""
";
        }

        private static string BuildUserBlock(AiClassifyRequest req)
        {
            var catArr = req.Categories.Select(c =>
                $@"{{ ""rel_path"": ""{c.RelPath}"", ""display"": ""{c.Display}"", ""keywords"": {(c.Keywords == null ? "null" : "[" + string.Join(",", c.Keywords.Select(k => $@"""{k}""")) + "]")}, ""ext_filter"": {(c.ExtFilter == null ? "null" : $@"""{c.ExtFilter}""")}, ""ai_hint"": {(c.AiHint == null ? "null" : $@"""{c.AiHint}""")} }}").ToArray();
            var cats = string.Join(",", catArr);

            var text = req.ExtractedText ?? "";
            if (text.Length > 9000) text = text[..9000];

            var sb = new StringBuilder();
            sb.AppendLine($@"- 設定: Base=""{req.BasePath}"", Uncategorized=""{req.UncategorizedRelPath}""");
            sb.AppendLine($@"- カテゴリ一覧({catArr.Length}): [{cats}]");
            sb.AppendLine($@"- 対象ファイル:");
            sb.AppendLine($@"  {{""name"":""{req.File.Name}"",""ext"":""{req.File.Ext}"",""path"":""{req.File.FullPath}"",""mtime_iso"":""{req.File.Mtime:O}"",""size_bytes"":{req.File.SizeBytes}}}");
            sb.AppendLine("- 抽出テキスト（空可）:");
            // ← ここを通常の補間文字列にして \" を使う
            sb.AppendLine($"\"\"\"{text}\"\"\"");
            return sb.ToString();
        }

        private async Task<string> CallGeminiAsync(string system, string user, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_apiKey}");

            var payload = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                systemInstruction = new { role = "system", parts = new[] { new { text = system } } },
                generationConfig = new { temperature = 0.2, maxOutputTokens = 512 }
            };
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            // CancellationToken 付きオーバーロードが無い環境もあるので無引数に
            var json = await res.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("empty response");
            return text!;
        }

        private static AiClassifyResult Parse(string jsonText, AiClassifyRequest req)
        {
            // JSON以外で返ってきた場合の掃除を強化
            var cleaned = jsonText.Trim();
            if (cleaned.StartsWith("```"))
            {
                // ```json ... ``` / ``` ... ``` の両方を許容
                cleaned = cleaned.Trim('`').Trim();
                if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned[4..].Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var path = root.GetProperty("classified_path").GetString() ?? req.UncategorizedRelPath;
            var conf = root.GetProperty("confidence").GetDouble();
            var sum = root.GetProperty("summary").GetString() ?? "";
            var tags = root.GetProperty("tags").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
            var rsn = root.GetProperty("reason").GetString() ?? "";

            // 低信頼は未分類へ
            if (conf < 0.55) path = req.UncategorizedRelPath;

            // 要約は最大50文字
            if (sum.Length > 50) sum = sum.Substring(0, 50);

            return new AiClassifyResult(path, conf, sum, tags, rsn);
        }
    }
}
