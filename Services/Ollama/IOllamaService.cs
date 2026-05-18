namespace SmartReviewSystem.Services.Ollama;

public interface IOllamaService
{
    IAsyncEnumerable<string> StreamSectionSummaryAsync(string heading, string content, CancellationToken ct = default);
}
