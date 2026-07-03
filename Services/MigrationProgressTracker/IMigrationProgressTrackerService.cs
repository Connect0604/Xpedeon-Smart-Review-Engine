namespace SmartReviewSystem.Services.MigrationProgressTracker;

using SmartReviewSystem.Models;

internal interface IMigrationProgressTrackerService
{
    Task<MigrationProgressTrackerViewModel> GetDashboardAsync(CancellationToken cancellationToken);
}

