using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Ai;
using SmartReviewSystem.Services.Ollama;

namespace SmartReviewSystem.Services.Orchestration;

public sealed class LlmRoutingStrategy : IRoutingStrategy
{
    private readonly IOllamaService _ollama;
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _hubPrompt;

    public LlmRoutingStrategy(IOllamaService ollama, HttpClient http, IConfiguration config)
    {
        _ollama = ollama;
        _http = http;
        _baseUrl = config["Validation:Controls:OllamaBaseUrl"] ?? "http://localhost:11434";
        _model = config["Validation:Controls:Model"] ?? "gpt-oss:20b-cloud";
        _hubPrompt = config["Validation:HubPrompt"] ?? "You are a routing agent. Decide which analysis checks apply to this document section. Return ONLY a JSON array of check labels.";
    }

    public RoutingMode Mode => RoutingMode.Dynamic;

    public async Task<IReadOnlyList<SectionPromptStep>> ResolveStepsAsync(
        string heading, string content, CancellationToken ct = default)
    {
        var allSteps = _ollama.GetAllAvailableSteps();
        if (allSteps.Count == 0)
            return Array.Empty<SectionPromptStep>();

        try
        {
            var selectedLabels = await CallHubAsync(heading, content, allSteps, ct);

            if (selectedLabels.Count == 0)
                return allSteps;

            return allSteps
                .Where(s => selectedLabels.Contains(s.Label, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            // fallback: return all steps if hub LLM call fails
            return allSteps;
        }
    }

    private async Task<List<string>> CallHubAsync(
        string heading, string content,
        IReadOnlyList<SectionPromptStep> allSteps,
        CancellationToken ct)
    {
        var contentPreview = content.Length > 400 ? content[..400] + "..." : content;
        var stepMenu = string.Join("\n", allSteps.Select(s =>
            $"- \"{s.Label}\": {FirstSentence(s.Prompt)}"));

        var hubPrompt = $"""
            {_hubPrompt}

            Section heading: {heading}
            Section content preview:
            {contentPreview}

            Available checks:
            {stepMenu}
            """;

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            prompt = hubPrompt,
            stream = false,
            format = "json"
        });

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/generate")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var responseText = doc.RootElement.GetProperty("response").GetString() ?? "[]";

        return ParseLabelArray(responseText);
    }

    private static string FirstSentence(string prompt)
    {
        var end = prompt.IndexOfAny(new[] { '.', '\n' });
        var sentence = end > 0 ? prompt[..end].Trim() : prompt.Trim();
        return sentence.Length > 100 ? sentence[..100] + "..." : sentence;
    }

    private static List<string> ParseLabelArray(string text)
    {
        try
        {
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end <= start)
                return new List<string>();

            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement
                .EnumerateArray()
                .Select(el => el.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
