namespace SmartReviewSystem.Models.Ai;

public sealed class MasterPromptConfig
{
    public List<SectionPromptStep> CommonPrompts { get; set; } = new();
    public List<SectionPromptConfig> Sections { get; set; } = new();
}

public sealed class SectionPromptConfig
{
    public string Name { get; set; } = string.Empty;
    public List<SectionPromptStep> Prompts { get; set; } = new();
}

public sealed class SectionPromptStep
{
    public string Label { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public OutputSchemaConfig? OutputSchema { get; set; }
}

public sealed class OutputSchemaConfig
{
    public List<OutputFieldConfig> Fields { get; set; } = new();
}

public sealed class OutputFieldConfig
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>text | list | boolean</summary>
    public string Type { get; set; } = "text";
}
