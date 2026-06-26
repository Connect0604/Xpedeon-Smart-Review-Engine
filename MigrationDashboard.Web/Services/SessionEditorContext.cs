namespace MigrationDashboard.Web.Services;

public sealed class SessionEditorContext : ISessionEditorContext
{
    public string? EditorName { get; private set; }

    public void SetEditorName(string editorName)
    {
        EditorName = string.IsNullOrWhiteSpace(editorName) ? null : editorName.Trim();
    }
}
