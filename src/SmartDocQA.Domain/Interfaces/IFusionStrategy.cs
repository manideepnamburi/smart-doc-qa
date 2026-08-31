using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Fuses results from dense + sparse + graph retrievers.
/// Default implementation uses Reciprocal Rank Fusion (RRF).
/// </summary>
public interface IFusionStrategy
{
    List<FusedChunk> Fuse(
        List<RetrievedChunk> denseResults,
        List<RetrievedChunk> sparseResults,
        List<RetrievedChunk>? graphResults,
        int topK);
}