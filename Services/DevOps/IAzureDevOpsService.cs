using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal interface IAzureDevOpsService
{
    Task<List<DevOpsStoryItem>> GetStoriesWithAttachmentsAsync(string organization, string project, string patToken, string wiqlCondition, CancellationToken cancellationToken, bool includeRevisionMetadata = false);
    Task<string> DownloadAttachmentTextAsync(string attachmentUrl, string patToken, CancellationToken cancellationToken);
    Task LoadImplementationDetailsAsync(DevOpsStoryItem story, string organization, string project, string patToken, CancellationToken cancellationToken);
    Task LoadPhaseHistoryAsync(DevOpsStoryItem story, string organization, string project, string patToken, CancellationToken cancellationToken);
    Task<List<string>> GetMfeModulesAsync(string organization, string project, string patToken, CancellationToken cancellationToken);
}
