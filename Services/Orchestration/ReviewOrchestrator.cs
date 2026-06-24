using System.Text.Json;
using SmartReviewSystem.Models.Agents;
using SmartReviewSystem.Models.Ai;
using SmartReviewSystem.Services.Ollama;

namespace SmartReviewSystem.Services.Orchestration;

public sealed class ReviewOrchestrator
{
    private readonly IOllamaService _ollama;
    private readonly IPocoDbComparisonService _pocoDbComparison;
    private readonly IDevExpressPropertyValidationService _devExpressValidator;

    public ReviewOrchestrator(
        IOllamaService ollama,
        IPocoDbComparisonService pocoDbComparison,
        IDevExpressPropertyValidationService devExpressValidator)
    {
        _ollama = ollama;
        _pocoDbComparison = pocoDbComparison;
        _devExpressValidator = devExpressValidator;
    }

    public async Task RunAsync(
        string heading,
        string content,
        IReadOnlyList<SectionPromptStep> steps,
        List<SpokeResult> results,
        Action onUpdate,
        CancellationToken ct = default)
    {
        var tasks = steps.Select((step, i) =>
            ExecuteSpokeAsync(step, i, heading, content, results, onUpdate, ct));

        await Task.WhenAll(tasks);
    }

    private async Task ExecuteSpokeAsync(
        SectionPromptStep step,
        int index,
        string heading,
        string content,
        List<SpokeResult> results,
        Action onUpdate,
        CancellationToken ct)
    {
        var result = results[index];
        result.Status = SpokeStatus.Running;
        onUpdate();

        try
        {
            var hasSchema = step.OutputSchema?.Fields.Count > 0;

            if (_pocoDbComparison.CanHandle(heading, step))
            {
                result.RawResult = await _pocoDbComparison.ExecuteAsync(heading, content, ct);
                onUpdate();
            }
            else if (_devExpressValidator.CanHandle(heading, step))
            {
                result.RawResult = await _devExpressValidator.ExecuteAsync(heading, content, ct);
                onUpdate();
            }
            else
            {
                await foreach (var token in _ollama.StreamStepAsync(heading, step, content, ct))
                {
                    result.RawResult += token;
                    if (!hasSchema) onUpdate();
                }
            }

            TryParseResult(result, step);
            result.Status = SpokeStatus.Done;
        }
        catch (OperationCanceledException)
        {
            result.Status = SpokeStatus.Failed;
            result.Error = "Cancelled";
        }
        catch (Exception ex)
        {
            result.Status = SpokeStatus.Failed;
            result.Error = $"Failed: {ex.Message}";
        }

        onUpdate();
    }

    private static void TryParseResult(SpokeResult result, SectionPromptStep step)
    {
        if (step.OutputSchema?.Fields.Count is null or 0 || string.IsNullOrWhiteSpace(result.RawResult))
            return;

        try
        {
            var start = result.RawResult.IndexOf('{');
            var end = result.RawResult.LastIndexOf('}');
            var json = start >= 0 && end > start
                ? result.RawResult[start..(end + 1)]
                : result.RawResult;

            using var doc = JsonDocument.Parse(json);
            result.Parsed = doc.RootElement
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch { }
    }
}
