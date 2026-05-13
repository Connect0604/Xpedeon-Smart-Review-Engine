namespace SmartReviewSystem.Models.Ui;

internal sealed class SectionFilterTabOptions
{
    public string Tab { get; init; } = string.Empty;
    public string Patten { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public List<string> Pattens { get; init; } = new();
    public List<string> Patterns { get; init; } = new();
}
