using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Orchestration;

public sealed class DevExpressPropertyValidationService : IDevExpressPropertyValidationService
{
    private readonly HttpClient _http;
    private readonly List<SectionPromptConfig> _sectionPrompts;

    public DevExpressPropertyValidationService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _sectionPrompts = configuration
            .GetSection("Validation:MasterPrompt:Sections")
            .Get<List<SectionPromptConfig>>() ?? new List<SectionPromptConfig>();
    }

    public bool CanHandle(string heading, SectionPromptStep step) =>
        heading.Contains("Razor Page", StringComparison.OrdinalIgnoreCase) &&
        step.Label.Equals("DevExpress Control Property Validation", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExecuteAsync(string heading, string content, CancellationToken ct = default)
    {
        var section = _sectionPrompts.FirstOrDefault(s => heading.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
        var mcpUrl = section?.RemoteMCPServer?.Url;
        if (string.IsNullOrWhiteSpace(mcpUrl))
        {
            return BuildError("Remote MCP server URL is not configured for Razor Page section.");
        }

        var toolName = section?.Tool;
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            toolName = await ResolveToolNameAsync(mcpUrl!, ct);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return BuildError("Unable to auto-resolve a DevExpress MCP tool name.");
            }
        }

        var toolArgs = BuildToolArguments(toolName!, content);
        var call = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = toolArgs
            }
        };

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(call), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return BuildError($"DevExpress MCP HTTP {(int)resp.StatusCode}: {raw}");
            }

            var text = ExtractToolText(raw);
            return JsonSerializer.Serialize(new
            {
                devexpress_deviations = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["control"] = "MCP Output",
                        ["property"] = "-",
                        ["severity"] = "info",
                        ["deviation"] = text,
                        ["recommended_fix"] = "Review listed deviations and align Razor properties with DevExpress docs."
                    }
                }
            });
        }
        catch (Exception ex)
        {
            return BuildError(ex.Message);
        }
    }

    private async Task<string?> ResolveToolNameAsync(string mcpUrl, CancellationToken ct)
    {
        var listReqPayload = new { jsonrpc = "2.0", id = 100, method = "tools/list", @params = new { } };
        var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(listReqPayload), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var json = NormalizeSseRaw(raw);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        string? searchTool = null;
        string? contentTool = null;
        foreach (var t in tools.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var n)) continue;
            var name = n.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            first ??= name;
            if (name.Equals("devexpress_docs_search", StringComparison.OrdinalIgnoreCase))
            {
                searchTool = name;
            }
            if (name.Equals("devexpress_docs_get_content", StringComparison.OrdinalIgnoreCase))
            {
                contentTool = name;
            }
            if (name.Contains("devexpress", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("dx", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("property", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("validate", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer search-first tool if available.
                if (!string.IsNullOrWhiteSpace(searchTool)) return searchTool;
                return name;
            }
        }

        if (!string.IsNullOrWhiteSpace(searchTool)) return searchTool;
        if (!string.IsNullOrWhiteSpace(contentTool)) return contentTool;
        return first;
    }

    private static object BuildToolArguments(string toolName, string razorContent)
    {
        var compactCode = razorContent.Length > 5000 ? razorContent[..5000] : razorContent;
        if (toolName.Equals("devexpress_docs_search", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                query = "Validate DevExpress control properties in this Razor code and list invalid or unsupported properties with fixes:\n" + compactCode
            };
        }

        if (toolName.Equals("devexpress_docs_get_content", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                url = "https://docs.devexpress.com/",
                query = "Find property validation guidance for DevExpress controls in this Razor code:\n" + compactCode
            };
        }

        return new
        {
            razorCode = compactCode,
            source = compactCode,
            content = compactCode
        };
    }

    private static string BuildError(string message)
    {
        return JsonSerializer.Serialize(new
        {
            devexpress_deviations = new[]
            {
                new Dictionary<string, string>
                {
                    ["control"] = "-",
                    ["property"] = "-",
                    ["severity"] = "error",
                    ["deviation"] = message,
                    ["recommended_fix"] = "Fix MCP configuration/integration and rerun validation."
                }
            }
        });
    }

    private static string ExtractToolText(string rawResponse)
    {
        var normalized = NormalizeSseRaw(rawResponse);
        using var doc = JsonDocument.Parse(normalized);
        if (!doc.RootElement.TryGetProperty("result", out var result))
        {
            return normalized;
        }

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                {
                    sb.AppendLine(text.GetString());
                }
            }

            var joined = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
            }
        }

        return result.ToString();
    }

    private static string NormalizeSseRaw(string raw)
    {
        var normalized = raw?.Trim() ?? string.Empty;
        var dataIndex = normalized.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
        if (dataIndex >= 0)
        {
            var after = normalized[(dataIndex + "data:".Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(after))
            {
                return after;
            }
        }

        return normalized;
    }
}
