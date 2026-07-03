namespace SmartReviewSystem.Models.DevOps;

internal sealed class FieldDefinitionResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("referenceName")]
    public string? ReferenceName { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("allowedValues")]
    public List<string> AllowedValues { get; init; } = new();
}

