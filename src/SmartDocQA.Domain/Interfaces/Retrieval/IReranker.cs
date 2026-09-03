using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Re-scores fused chunks against the original query for precision.
/// Default: Claude-as-reranker. Swappable with cross-encoder ONNX model.
/// </summary>
public interface IReranker
{
    Task<List<RankedChunk>> RerankAsync(string query, List<FusedChunk> candidates, int topK, CancellationToken ct = default);
}