namespace MigrationDashboard.Web.Services;

public interface IEditorIdentityAccessor
{
    Task<string?> GetCurrentEditorAsync(CancellationToken cancellationToken = default);
}
