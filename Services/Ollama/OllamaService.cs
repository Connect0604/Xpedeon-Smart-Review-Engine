using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Ollama;

public sealed class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly List<string> _fallbackModels;
    private readonly string _genericPrompt;
    private readonly List<SectionPromptStep> _commonPrompts;
    private readonly List<SectionPromptConfig> _sectionPrompts;
    private string? _lastUsedModel;
    private bool _lastCallUsedFallback;

    private const string GenerateEndpoint = "/api/generate";

    public OllamaService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _baseUrl = configuration["Validation:Controls:OllamaBaseUrl"] ?? "http://localhost:11434";
        _model = configuration["Validation:Controls:Model"] ?? "gpt-oss:20b-cloud";
        _fallbackModels = configuration.GetSection("Validation:Controls:FallbackModels").Get<List<string>>() ?? new List<string>();
        _genericPrompt = configuration["Validation:GenericPrompt"] ?? string.Empty;
        _commonPrompts = configuration
            .GetSection("Validation:MasterPrompt:CommonPrompts")
            .Get<List<SectionPromptStep>>() ?? new List<SectionPromptStep>();
        _sectionPrompts = configuration
            .GetSection("Validation:MasterPrompt:Sections")
            .Get<List<SectionPromptConfig>>() ?? new List<SectionPromptConfig>();
    }

    public bool HasSectionPrompt(string heading) =>
        _commonPrompts.Count > 0 ||
        _sectionPrompts.Any(s => SectionNameMatches(s.Name, heading) && s.Prompts.Count > 0);

    public IReadOnlyList<SectionPromptStep> GetPromptSteps(string heading)
    {
        var sectionSteps = _sectionPrompts
            .FirstOrDefault(s => SectionNameMatches(s.Name, heading))
            ?.Prompts ?? new List<SectionPromptStep>();

        return _commonPrompts.Concat(sectionSteps).ToList();
    }

    // Matches "Razor Page" against "16. Razor Page", "A. Razor Page", or an exact heading.
    private static bool SectionNameMatches(string configName, string heading) =>
        heading.Contains(configName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SectionPromptStep> GetAllAvailableSteps()
    {
        var sectionSteps = _sectionPrompts.SelectMany(s => s.Prompts);
        return _commonPrompts.Concat(sectionSteps)
            .GroupBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public string GetConfiguredModelsDisplay()
    {
        var models = GetModelCandidates();
        return string.Join(" -> ", models);
    }

    public string GetPrimaryModel() => _model;

    public string? GetLastUsedModel() => _lastUsedModel;
    public bool WasLastCallFallbackUsed() => _lastCallUsedFallback;

    public async IAsyncEnumerable<string> StreamStepAsync(
        string heading,
        SectionPromptStep step,
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hasSchema = step.OutputSchema?.Fields.Count > 0;
        var prompt = BuildPrompt(step, heading, content);
        var result = await GenerateWithFallbackAsync(prompt, formatJson: hasSchema, ct);
        if (!string.IsNullOrWhiteSpace(result))
        {
            yield return result;
        }
    }

    public async Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default)
    {
        return await GenerateWithFallbackAsync(prompt, formatJson: true, ct);
    }

    public async Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        return await GenerateWithFallbackAsync(prompt, formatJson: false, ct);
    }

    private async Task<string> GenerateWithFallbackAsync(string prompt, bool formatJson, CancellationToken ct)
    {
        Exception? lastError = null;
        _lastCallUsedFallback = false;
        foreach (var model in GetModelCandidates())
        {
            try
            {
                var requestObj = formatJson
                    ? (object)new { model, prompt, stream = false, format = "json" }
                    : new { model, prompt, stream = false };

                var body = JsonSerializer.Serialize(requestObj);
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + GenerateEndpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("response", out var responseText))
                {
                    throw new InvalidOperationException("LLM response did not include a 'response' property.");
                }

                _lastUsedModel = model;
                _lastCallUsedFallback = !string.Equals(model, _model, StringComparison.OrdinalIgnoreCase);
                return responseText.GetString() ?? string.Empty;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("All configured models failed.", lastError);
    }

    private List<string> GetModelCandidates()
    {
        return new[] { _model }
            .Concat(_fallbackModels ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildPrompt(SectionPromptStep step, string heading, string content)
    {
        var jsonInstruction = step.OutputSchema?.Fields.Count > 0
            ? BuildJsonInstruction(step.OutputSchema)
            : string.Empty;

        return $"""
            {_genericPrompt}

            {step.Prompt}

            Section heading: {heading}

            Section content:
            {content}
            {jsonInstruction}
            """;
    }

    private static string BuildJsonInstruction(OutputSchemaConfig schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Return ONLY a valid JSON object with these exact keys, no explanation or text outside the JSON:");
        sb.AppendLine("{");

        for (var i = 0; i < schema.Fields.Count; i++)
        {
            var field = schema.Fields[i];
            var comma = i < schema.Fields.Count - 1 ? "," : "";
            var example = field.Type switch
            {
                "list"    => "[\"<value>\"]",
                "boolean" => "true",
                "table"   => "[{\"label\":\"<display name or ->\",\"component\":\"<control type>\",\"binding\":\"<bound variable or ->\"}]",
                _         => "\"<value>\""
            };
            sb.AppendLine($"  \"{field.Key}\": {example}{comma}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}
