using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Search
{
    public sealed class GeminiChat
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        public GeminiChat(string apiKey)
        {
            _apiKey = apiKey;
            _http = new HttpClient();
        }

        public async Task<string> AskJsonAsync(string prompt, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_apiKey}");

            var body = new
            {
                contents = new[]
                {
                    new {
                        role = "user",
                        parts = new [] { new { text = prompt } }
                    }
                }
            };

            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            string json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.GetArrayLength() > 0)
            {
                return candidates[0].GetProperty("content")
                                    .GetProperty("parts")[0]
                                    .GetProperty("text").GetString() ?? "";
            }
            return "";
        }
    }
}
