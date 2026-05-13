namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsAttachmentItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public bool IsSupported { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string AttachedBy { get; init; } = "Unknown";
    public DateTimeOffset? AttachedOn { get; init; }

    public string DisplaySize => SizeBytes.HasValue ? $"{Math.Round(SizeBytes.Value / 1024d, 1)} KB" : "Unknown size";
    public string DisplayAttachedOn => AttachedOn.HasValue ? AttachedOn.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Unknown date";
}
