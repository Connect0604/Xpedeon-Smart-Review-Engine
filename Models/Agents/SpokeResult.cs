using System.Text.Json;
using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Models.Agents;

public enum SpokeStatus { Pending, Running, Done, Failed }

public sealed class SpokeResult
{
    public string Label { get; set; } = string.Empty;
    public string RawResult { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? Parsed { get; set; }
    public SpokeStatus Status { get; set; } = SpokeStatus.Pending;
}
