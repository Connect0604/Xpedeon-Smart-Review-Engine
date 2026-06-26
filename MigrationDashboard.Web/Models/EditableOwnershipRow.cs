namespace MigrationDashboard.Web.Models;

public sealed class EditableOwnershipRow
{
    public required OwnershipObjectKey Key { get; init; }
    public required string Layer { get; init; }
    public required string ObjectName { get; init; }
    public required string ObjectType { get; init; }
    public required string OwnershipCategory { get; set; }
    public string? Remarks { get; set; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public bool IsSaving { get; set; }
    public string? StatusMessage { get; set; }
    public bool HasError { get; set; }

    public static EditableOwnershipRow FromRow(OwnershipObjectRow row)
    {
        return new EditableOwnershipRow
        {
            Key = row.Key,
            Layer = row.Layer,
            ObjectName = row.ObjectName,
            ObjectType = row.ObjectType,
            OwnershipCategory = row.OwnershipCategory,
            Remarks = row.Remarks,
            CreatedBy = row.CreatedBy,
            CreatedDate = row.CreatedDate,
            ModifiedBy = row.ModifiedBy,
            ModifiedDate = row.ModifiedDate
        };
    }
}
