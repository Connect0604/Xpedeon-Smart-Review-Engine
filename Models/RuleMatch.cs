namespace SmartReviewSystem.Pages;

internal sealed class RuleMatch
{
    public int Index { get; init; }
    public string Matched { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
}
