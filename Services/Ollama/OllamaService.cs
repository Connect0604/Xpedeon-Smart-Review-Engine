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
    private readonly string _genericPrompt;
    private readonly List<SectionPromptStep> _commonPrompts;
    private readonly List<SectionPromptConfig> _sectionPrompts;

    private const string Model = "gpt-oss:20b-cloud";
    private const string GenerateEndpoint = "/api/generate";

    public OllamaService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _baseUrl = configuration["Validation:Controls:OllamaBaseUrl"] ?? "http://localhost:11434";
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
        _sectionPrompts.Any(s => s.Name.Equals(heading, StringComparison.OrdinalIgnoreCase)
                               && s.Prompts.Count > 0);

    public IReadOnlyList<SectionPromptStep> GetPromptSteps(string heading)
    {
        var sectionSteps = _sectionPrompts
            .FirstOrDefault(s => s.Name.Equals(heading, StringComparison.OrdinalIgnoreCase))
            ?.Prompts ?? new List<SectionPromptStep>();

        return _commonPrompts.Concat(sectionSteps).ToList();
    }

    public async IAsyncEnumerable<string> StreamStepAsync(
        string heading,
        SectionPromptStep step,
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hasSchema = step.OutputSchema?.Fields.Count > 0;

        var requestObj = hasSchema
            ? (object)new { model = Model, prompt = BuildPrompt(step, heading, content), stream = true, format = "json" }
            : new { model = Model, prompt = BuildPrompt(step, heading, content), stream = true };

        var body = JsonSerializer.Serialize(requestObj);

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
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("response", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                    yield return token;
            }

            if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                break;
        }
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
                _         => "\"<value>\""
            };
            sb.AppendLine($"  \"{field.Key}\": {example}{comma}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}
