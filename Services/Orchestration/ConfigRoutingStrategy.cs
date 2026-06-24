using SmartReviewSystem.Models.Ai;
using SmartReviewSystem.Services.Ollama;

namespace SmartReviewSystem.Services.Orchestration;

public sealed class ConfigRoutingStrategy : IRoutingStrategy
{
    private readonly IOllamaService _ollama;

    public ConfigRoutingStrategy(IOllamaService ollama) => _ollama = ollama;

    public RoutingMode Mode => RoutingMode.Static;

    public Task<IReadOnlyList<SectionPromptStep>> ResolveStepsAsync(
        string heading, string content, CancellationToken ct = default)
        => Task.FromResult(_ollama.GetPromptSteps(heading));
}
