namespace SmartReviewSystem.Models.DevOps;

internal sealed class FieldsListResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("count")]
    public int Count { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public List<FieldInfo> Value { get; init; } = new();
}

internal sealed class FieldInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("referenceName")]
    public string? ReferenceName { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("isIdentity")]
    public bool IsIdentity { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("isPicklistOptional")]
    public bool IsPicklistOptional { get; init; }
}
