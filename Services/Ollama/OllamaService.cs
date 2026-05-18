using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SmartReviewSystem.Services.Ollama;

public sealed class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private const string Model = "gpt-oss:20b-cloud";
    private const string GenerateEndpoint = "/api/generate";

    public OllamaService(HttpClient http, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _http = http;
        _baseUrl = configuration["Validation:Controls:OllamaBaseUrl"] ?? "http://localhost:11434";
    }

    public async IAsyncEnumerable<string> StreamSectionSummaryAsync(
        string heading,
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            prompt = BuildPrompt(heading, content),
            stream = true
        });

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + GenerateEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("response", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                {
                    yield return token;
                }
            }

            if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
            {
                break;
            }
        }
    }

    private static string BuildPrompt(string heading, string content) => $"""
        You are a technical document reviewer. Summarize the following section from a technical specification document.

        Focus on:
        - What this section is about
        - Key requirements or decisions mentioned
        - Any notable concerns or gaps

        Section heading: {heading}

        Section content:
        {content}

        Provide a concise summary in 3-5 sentences.
        """;
}
