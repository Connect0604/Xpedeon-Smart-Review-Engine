namespace MigrationDashboard.Web.Models;

public sealed class EditableChangeEventRow
{
    public required int DetailId { get; init; }
    public required int EventId { get; init; }
    public required int FormId { get; init; }
    public required string FormName { get; init; }
    public required string ProcessCode { get; init; }
    public required string StepCode { get; init; }
    public required string BuildNumber { get; init; }
    public required string BranchName { get; init; }
    public required string ChangedBy { get; init; }
    public required string CommitId { get; init; }
    public required DateTime EventTimestamp { get; init; }
    public required string ObjectName { get; init; }
    public required string Layer { get; init; }
    public required string ObjectType { get; init; }
    public required string OwnershipCategory { get; init; }
    public required string ReviewStatus { get; set; }
    public string? Remarks { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public bool IsSaving { get; set; }
    public string? StatusMessage { get; set; }
    public bool HasError { get; set; }
}
