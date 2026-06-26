namespace MigrationDashboard.Web.Models;

public sealed class EditorLoginPostModel
{
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? ReturnUrl { get; set; }
}
