using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Orchestration;

public interface IDevExpressPropertyValidationService
{
    bool CanHandle(string heading, SectionPromptStep step);
    Task<string> ExecuteAsync(string heading, string content, CancellationToken ct = default);
}

