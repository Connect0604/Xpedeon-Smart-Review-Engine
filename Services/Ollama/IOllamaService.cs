using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Ollama;

public interface IOllamaService
{
    bool HasSectionPrompt(string heading);
    IReadOnlyList<SectionPromptStep> GetPromptSteps(string heading);
    IAsyncEnumerable<string> StreamStepAsync(string heading, SectionPromptStep step, string content, CancellationToken ct = default);
}
