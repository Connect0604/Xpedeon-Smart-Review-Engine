namespace SmartReviewSystem.Pages;

internal sealed class Violation
{
    public string RuleId { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Matched { get; init; } = string.Empty;
    public string Fix { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
