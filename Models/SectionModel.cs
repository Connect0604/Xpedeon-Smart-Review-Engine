namespace SmartReviewSystem.Pages;

internal sealed class SectionModel
{
    public string Id { get; init; } = string.Empty;
    public string Letter { get; init; } = string.Empty;
    public string Heading { get; init; } = string.Empty;
    public int LineStart { get; init; }
    public int LineCount { get; init; }
    public string Content { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
}
