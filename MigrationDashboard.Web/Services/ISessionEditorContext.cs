namespace MigrationDashboard.Web.Services;

public interface ISessionEditorContext
{
    string? EditorName { get; }
    void SetEditorName(string editorName);
}
