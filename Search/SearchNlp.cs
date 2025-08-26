// Explore/Search/SearchNlp.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Searching
{
    public interface ISearchNlp
    {
        Task<string?> NormalizeAsync(string text, CancellationToken ct);
    }

    /// <summary>
    /// Gemini を使った検索クエリ正規化。
    /// - 1回呼び出して JSON をそのまま返す（再依頼/ローカル整形はしない）
    /// - DebugLog に [NLP/*] を出力
    /// </summary>
    public sealed class GeminiSearchNlp : ISearchNlp, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _model;

        public Action<string>? DebugLog { get; set; }

        public GeminiSearchNlp(string apiKey, string model = "gemini-1.5-flash-latest", HttpClient? httpClient = null)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _model = string.IsNullOrWhiteSpace(model) ? "gemini-1.5-flash-latest" : model;
            _http = httpClient ?? new HttpClient();
        }

        public void Dispose() => _http.Dispose();

        public async Task<string?> NormalizeAsync(string text, CancellationToken ct)
        {
            Log($"[NLP/input] {text}");

            var prompt = BuildNormalizePrompt(text);
            var raw = await CallAsync(prompt, ct);
            var json = ExtractJson(raw);
            if (json == null)
            {
                Log("[NLP/first_json] (none)");
                return null;
            }

            Log($"[NLP/first_json] {json}");
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var e in terms.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString() ?? "");
                    Log($"[NLP/final_terms] [{string.Join(",", list)}]");
                }
            }
            catch { /* ignore */ }

            return json;
        }

        // ---- Gemini 呼び出し ----
        private async Task<string> CallAsync(string userText, CancellationToken ct)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            var payload = JsonSerializer.Serialize(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
                generationConfig = new
                {
                    response_mime_type = "application/json",
                    temperature = 0.2
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var cands = doc.RootElement.GetProperty("candidates");
                if (cands.GetArrayLength() == 0) return body;
                var parts = cands[0].GetProperty("content").GetProperty("parts");
                if (parts.GetArrayLength() == 0) return body;
                return parts[0].GetProperty("text").GetString() ?? body;
            }
            catch
            {
                return body;
            }
        }

        // ---- プロンプト ----
        private static string BuildNormalizePrompt(string input)
        {
            var today = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);
            int deltaToMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-deltaToMonday);
            var weekEnd = weekStart.AddDays(7);

            string TODAY_ISO = today.ToString("yyyy-MM-dd");
            string YESTERDAY_ISO = yesterday.ToString("yyyy-MM-dd");
            string WEEK_START_ISO = weekStart.ToString("yyyy-MM-dd");
            string WEEK_END_ISO = weekEnd.ToString("yyyy-MM-dd");

            var prompt = @"
あなたは “検索クエリ正規化エンジン” です。入力は日本語の自然文です。
出力は JSON オブジェクト**のみ**。コードフェンス・前後説明・余計なキー厳禁。キー順は下記**厳守**。

{
  ""terms"": [],            // コア語（名詞中心・各8文字以内・最大8語）
  ""softTerms"": [],        // 表記ゆれ/同義語（各8文字以内・最大8語）※少数厳選
  ""extList"": [],          // 拡張子（小文字・ドット無し）。例: [""pdf"",""docx"",""xlsx"",""png""]
  ""locationHints"": [],    // パスに現れやすい語（例: 学科名/部署名など）。最大3
  ""pathLikes"": [],        // 例: [""%/Downloads/%"",""%/資料/%""]
  ""dateRange"": {          // 期間。kind は ""modified"" または ""created""
    ""kind"": ""modified"",
    ""from"": null,         // 例: ""{{YESTERDAY_ISO}}"" / ""{{WEEK_START_ISO}}""
    ""to"": null            // 例: ""{{TODAY_ISO}}"" / ""{{WEEK_END_ISO}}""（排他上限）
  },
  ""limit"": 5,
  ""offset"": 0,
  ""variants"": {           // 表記ゆれセット
    ""year"": [],           // 例: [""R7"",""令和7年"",""令和七年"",""2025年""]
    ""term"": {             // 任意語→候補配列
      // 例: ""入学要項"": [""募集要項"",""受験案内""]
    }
  }
}

【厳格ルール】
1) JSON オブジェクトのみ（コードフェンス・説明禁止）。指定以外のキーは入れない。キー順厳守。
2) 相対日付は {{TODAY_ISO}} 基準。to は翌日0:00の**排他上限**とする。
3) 作り話の地名・校名・部署名を勝手に追加しない。
4) 拡張子:
   - 「過去問」「試験」「問題」→ [""pdf"",""jpg"",""jpeg"",""png""]
   - 「企画書」「報告書」「プレゼン」→ [""docx"",""xlsx"",""pptx"",""pdf""]
   - 「データ」「集計」「CSV」→ [""xlsx"",""csv""]
   - 「画像」→ [""jpg"",""jpeg"",""png"",""gif"",""webp""]
   - 明示指定があればそれを優先
5) 西暦/和暦/R7等の年は variants.year に入れる。terms/softTerms へは重複させない。
6) terms は冗長な文を避け、検索に有効な短い日本語の短語のみ。softTerms は控えめに。
7) 最大語数の目安: terms=8, softTerms=8, variants.year=4。

【Few-shot】
- 入力: ２年生の前期中間テストの数学の過去問ください
  出力: {""terms"":[""数学"",""過去問"",""2年"",""前期""],""softTerms"":[], ""extList"":[""pdf"",""jpg"",""jpeg"",""png""],""locationHints"":[],""pathLikes"":[],""dateRange"":{""kind"":""modified"",""from"":null,""to"":null},""limit"":5,""offset"":0,""variants"":{""year"":[],""term"":{}}}

- 入力: OneDriveの企画書PRJ_123 最終版 今週
  出力: {""terms"":[""企画書"",""最終版"",""PRJ_123""],""softTerms"":[], ""extList"":[""docx"",""xlsx"",""pptx"",""pdf""],""locationHints"":[""OneDrive""],""pathLikes"":[],""dateRange"":{""kind"":""modified"",""from"":""{{WEEK_START_ISO}}"",""to"":""{{WEEK_END_ISO}}""},""limit"":5,""offset"":0,""variants"":{""year"":[],""term"":{}}}

- 入力: R7 入学要項
  出力: {""terms"":[""入学要項""],""softTerms"":[""募集要項"",""受験案内""],""extList"":[], ""locationHints"":[],""pathLikes"":[],""dateRange"":{""kind"":""modified"",""from"":null,""to"":null},""limit"":5,""offset"":0,""variants"":{""year"":[""R7"",""令和7年"",""令和七年"",""2025年""],""term"":{""入学要項"":[""募集要項"",""受験案内""]}}}

【ユーザー入力】
{USER_TEXT}
".Trim();

            prompt = prompt
                .Replace("{{TODAY_ISO}}", TODAY_ISO)
                .Replace("{{YESTERDAY_ISO}}", YESTERDAY_ISO)
                .Replace("{{WEEK_START_ISO}}", WEEK_START_ISO)
                .Replace("{{WEEK_END_ISO}}", WEEK_END_ISO)
                .Replace("{USER_TEXT}", input ?? string.Empty);

            return prompt;
        }

        private static string? ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var i = text.IndexOf('{'); var j = text.LastIndexOf('}');
            if (i < 0 || j <= i) return null;
            return text.Substring(i, j - i + 1);
        }

        private void Log(string msg)
        {
            try { DebugLog?.Invoke(msg); } catch { }
            try { System.Diagnostics.Debug.WriteLine(msg); } catch { }
        }
    }
}
