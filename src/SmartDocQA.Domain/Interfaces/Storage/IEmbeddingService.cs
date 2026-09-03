namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Generates float[] embeddings for a list of texts.
/// Implementation calls Claude or any other embedding provider.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default);
}