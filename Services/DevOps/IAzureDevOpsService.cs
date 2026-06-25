using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal interface IAzureDevOpsService
{
    Task<List<DevOpsStoryItem>> GetStoriesWithAttachmentsAsync(string organization, string project, string patToken, string wiqlCondition, CancellationToken cancellationToken, bool includeRevisionMetadata = false);
    Task<string> DownloadAttachmentTextAsync(string attachmentUrl, string patToken, CancellationToken cancellationToken);
}
