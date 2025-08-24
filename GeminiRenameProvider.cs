using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Explore
{
    /// <summary>
    /// Gemini を使ってリネーム候補を生成するプロバイダ。
    /// 画像は縮小JPEGを parts.inline_data として送信（内容ベース）。
    /// テキストは抽出本文、抽出不可はメタ情報＋HEXヘッダを送信。
    /// </summary>
    public sealed class GeminiRenameProvider : IRenameAIProvider
    {
        private readonly string _apiKey;
        private readonly string _model;
        private readonly HttpClient _http = new HttpClient();
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public string Model => _model;

        public static GeminiRenameProvider? TryCreateFromEnv()
        {
            string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

            if (string.IsNullOrWhiteSpace(key))
            {
                try
                {
                    var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    var path = Path.Combine(exeDir, "gemini_api_key.txt");
                    if (File.Exists(path)) key = File.ReadAllText(path).Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                try
                {
                    var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var dir = Path.Combine(app, "Explore");
                    var path = Path.Combine(dir, "gemini_api_key.txt");
                    if (File.Exists(path)) key = File.ReadAllText(path).Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(key)) return null;

            var model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-1.5-flash";
            return new GeminiRenameProvider(key!, model);
        }

        public GeminiRenameProvider(string apiKey, string model = "gemini-1.5-flash")
        {
            _apiKey = apiKey;
            _model = string.IsNullOrWhiteSpace(model) ? "gemini-1.5-flash" : model;
        }

        // 疎通確認
        public async Task<bool> PingAsync()
        {
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                var payload = new
                {
                    contents = new object[]
                    {
                        new { parts = new object[]{ new { text = "ping" } } }
                    }
                };
                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var res = await _http.SendAsync(req);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task SuggestAsync(ObservableCollection<FileRenameItem> items)
        {
            foreach (var item in items)
            {
                try
                {
                    string suggestion;

                    if (ImageInfoExtractor.LooksLikeImage(item.OriginalFullPath))
                    {
                        var exifText = item.ContentSample ?? "";
                        suggestion = await AskGeminiForImageAsync(item, exifText);
                    }
                    else
                    {
                        var prompt = BuildTextPrompt(item);
                        suggestion = await CallGeminiAsync(prompt);
                    }

                    var name = ExtractNameFromReply(suggestion);
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileNameWithoutExtension(item.OriginalName);

                    item.SuggestedName = name;
                }
                catch
                {
                    // 続行
                }
            }
        }

        // --- 画像 ---
        private async Task<string> AskGeminiForImageAsync(FileRenameItem item, string exifText)
        {
            var (mime, bytes) = PrepareImageBytes(item.OriginalFullPath);
            var base64 = Convert.ToBase64String(bytes);
            var systemPrompt = BuildImagePrompt(item, exifText);

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            var payload = new
            {
                contents = new object[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = systemPrompt },
                            new { inline_data = new { mime_type = mime, data = base64 } }
                        }
                    }
                },
                generationConfig = new { temperature = 0.4, topP = 0.9, maxOutputTokens = 256 }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cand0 = root.GetProperty("candidates")[0];
            var text = cand0.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            return text;
        }

        private static (string mime, byte[] bytes) PrepareImageBytes(string path, int maxDim = 1280, long quality = 85L)
        {
            using var src = Image.FromFile(path);
            int w = src.Width, h = src.Height;
            if (w > maxDim || h > maxDim)
            {
                double s = Math.Min((double)maxDim / w, (double)maxDim / h);
                int nw = Math.Max(1, (int)Math.Round(w * s));
                int nh = Math.Max(1, (int)Math.Round(h * s));
                using var bmp = new Bitmap(nw, nh);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, 0, 0, nw, nh);
                }
                return ("image/jpeg", EncodeJpeg(bmp, quality));
            }
            else
            {
                return ("image/jpeg", EncodeJpeg(src, quality));
            }

            static byte[] EncodeJpeg(Image img, long q)
            {
                using var ms = new MemoryStream();
                var enc = GetEncoder(ImageFormat.Jpeg);
                var ps = new EncoderParameters(1);
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                img.Save(ms, enc, ps);
                return ms.ToArray();
            }

            static ImageCodecInfo GetEncoder(ImageFormat format)
            {
                var codecs = ImageCodecInfo.GetImageDecoders();
                foreach (var c in codecs) if (c.FormatID == format.Guid) return c;
                return ImageCodecInfo.GetImageDecoders()[0];
            }
        }

        private static string BuildImagePrompt(FileRenameItem item, string exifText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("あなたはファイルの内容に基づいて、わかりやすい日本語ファイル名を1つ提案するアシスタントです。");
            sb.AppendLine("以下の画像の内容と、参考情報（EXIF等）があればそれも考慮して、適切なファイル名を考えてください。");
            sb.AppendLine();
            sb.AppendLine("出力仕様:");
            sb.AppendLine("- 出力は必ず JSON のみ。コードブロックや説明は不要。");
            sb.AppendLine("- JSON のキーは name, reason, confidence。");
            sb.AppendLine("- name は拡張子を含めない。");
            sb.AppendLine("- 許可記号: - _ () [] と半角スペース。先頭末尾の空白/ドットは不可。");
            sb.AppendLine("- 3〜60文字程度で簡潔に。");
            sb.AppendLine();
            sb.AppendLine($"元のファイル名: {item.OriginalName}");
            sb.AppendLine($"拡張子: {item.Extension}");
            try
            {
                var fi = new FileInfo(item.OriginalFullPath);
                sb.AppendLine($"サイズ: {fi.Length} bytes");
                sb.AppendLine($"更新日時: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(exifText))
            {
                sb.AppendLine();
                sb.AppendLine("==== 参考情報（EXIF/画像情報の要約） ====");
                sb.AppendLine(exifText.Length > 1500 ? exifText[..1500] : exifText);
            }

            sb.AppendLine();
            sb.AppendLine("出力例:");
            sb.AppendLine("{\"name\":\"狼_雪原_夜の遠吠え\",\"reason\":\"被写体とシーンから命名\",\"confidence\":0.83}");
            sb.AppendLine("JSON のみを返してください。");
            return sb.ToString();
        }

        // --- テキスト ---
        private static string BuildTextPrompt(FileRenameItem item)
        {
            var sb = new StringBuilder();
            sb.AppendLine("あなたはファイルのわかりやすい日本語ファイル名を1つ提案するアシスタントです。");
            sb.AppendLine("出力は JSON のみ (name, reason, confidence)。name は拡張子を含めない。");
            sb.AppendLine("許可記号: - _ () [] と半角スペース。3〜60文字程度。先頭末尾の空白/ドットは不可。");
            sb.AppendLine();
            sb.AppendLine($"元のファイル名: {item.OriginalName}");
            sb.AppendLine($"拡張子: {item.Extension}");
            try
            {
                var fi = new FileInfo(item.OriginalFullPath);
                sb.AppendLine($"サイズ: {fi.Length} bytes");
                sb.AppendLine($"更新日時: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(item.ContentSample))
            {
                sb.AppendLine();
                sb.AppendLine("==== 抽出テキスト（冒頭） ====");
                var text = item.ContentSample.Replace("\u0000", " ");
                sb.AppendLine(text.Length > 4000 ? text[..4000] : text);
            }
            else
            {
                try
                {
                    var hex = ReadHexHead(item.OriginalFullPath, 2048);
                    if (!string.IsNullOrEmpty(hex))
                    {
                        sb.AppendLine();
                        sb.AppendLine("==== バイナリ先頭ヘッダ (hex) ====");
                        sb.AppendLine(hex);
                    }
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("出力例:");
            sb.AppendLine("{\"name\":\"議事録_第3回全社会議_2024-07-15\",\"reason\":\"本文見出しと日付から要約\",\"confidence\":0.86}");
            sb.AppendLine("JSON のみを返してください。");
            return sb.ToString();
        }

        private static string ReadHexHead(string path, int maxBytes)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var len = (int)Math.Min(maxBytes, fs.Length);
                var buf = new byte[len];

                // 安全に len バイトまで読み込む（CA2022対応）
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(buf, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == 0) return string.Empty;

                var sb = new StringBuilder(read * 2 + read / 32 + 8);
                for (int i = 0; i < read; i++)
                {
                    sb.Append(buf[i].ToString("X2"));
                    if ((i + 1) % 32 == 0) sb.AppendLine();
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        private async Task<string> CallGeminiAsync(string prompt)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            var payload = new
            {
                contents = new object[]
                {
                    new { parts = new object[]{ new { text = prompt } } }
                },
                generationConfig = new { temperature = 0.5, topP = 0.9, maxOutputTokens = 256 }
            };
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cand0 = root.GetProperty("candidates")[0];
            var text = cand0.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            return text;
        }

        private static string ExtractNameFromReply(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;
            var s = reply.Trim();
            if (s.StartsWith("```"))
            {
                var idx = s.IndexOf('\n');
                if (idx >= 0) s = s[(idx + 1)..];
                if (s.EndsWith("```")) s = s[..^3];
            }
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("name", out var el))
                {
                    var name = el.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name!;
                }
            }
            catch { }
            var line = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0].Trim();
            if (line.StartsWith("\"") && line.EndsWith("\"") && line.Length >= 2)
                line = line[1..^1];
            return line;
        }
    }
}
