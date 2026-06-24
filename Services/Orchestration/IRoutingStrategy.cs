using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Orchestration;

public enum RoutingMode { Static, Dynamic }

public interface IRoutingStrategy
{
    RoutingMode Mode { get; }

    Task<IReadOnlyList<SectionPromptStep>> ResolveStepsAsync(
        string heading,
        string content,
        CancellationToken ct = default);
}
