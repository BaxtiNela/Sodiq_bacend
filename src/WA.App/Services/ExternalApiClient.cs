using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WA.App.Services;

/// <summary>OpenAI-compatible API (DeepSeek, OpenAI) dan streaming javob oladi</summary>
public class ExternalApiClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static bool IsExternalModel(string model) =>
        model.StartsWith("deepseek") || model.StartsWith("gpt-") || model.StartsWith("o1") || model.StartsWith("o3");

    public static string BaseUrlFor(string model) =>
        model.StartsWith("deepseek")
            ? "https://api.deepseek.com/v1/chat/completions"
            : "https://api.openai.com/v1/chat/completions";

    public static string ProviderLabel(string model) =>
        model.StartsWith("deepseek") ? "DeepSeek" : "OpenAI";

    public async Task StreamAsync(
        string model,
        string apiKey,
        List<(string Role, string Content)> messages,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        var url = BaseUrlFor(model);

        var body = new
        {
            model,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("delta");

                if (delta.TryGetProperty("content", out var contentProp))
                {
                    var token = contentProp.GetString();
                    if (!string.IsNullOrEmpty(token))
                        onToken(token);
                }
            }
            catch { /* partial JSON — skip */ }
        }
    }
}
