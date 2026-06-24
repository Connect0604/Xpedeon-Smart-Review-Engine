using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Ollama;

public interface IOllamaService
{
    string GetConfiguredModelsDisplay();
    string GetPrimaryModel();
    string? GetLastUsedModel();
    bool WasLastCallFallbackUsed();
    bool HasSectionPrompt(string heading);
    IReadOnlyList<SectionPromptStep> GetPromptSteps(string heading);
    IReadOnlyList<SectionPromptStep> GetAllAvailableSteps();
    IAsyncEnumerable<string> StreamStepAsync(string heading, SectionPromptStep step, string content, CancellationToken ct = default);
    Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default);
    Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default);
}
