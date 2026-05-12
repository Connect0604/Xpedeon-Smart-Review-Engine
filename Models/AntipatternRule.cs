namespace SmartReviewSystem.Pages;

internal enum RuleProfile
{
    Any,
    Code,
    UserStory,
    ModuleSpec
}

internal sealed class AntipatternRule
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public RuleProfile Profile { get; init; } = RuleProfile.Any;
    public Func<string, List<RuleMatch>> Detect { get; init; } = _ => new List<RuleMatch>();
    public string Fix { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
