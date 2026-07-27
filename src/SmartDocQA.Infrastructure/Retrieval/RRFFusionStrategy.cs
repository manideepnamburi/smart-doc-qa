using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.Retrieval;

/// <summary>
/// Reciprocal Rank Fusion — merges dense, sparse, and graph results into one ranked list.
/// Formula: RRF(d) = Σ 1 / (k + rank_i)   where k=60 by default.
/// Proven to outperform weighted score combination for heterogeneous retrieval.
/// </summary>
public class RRFFusionStrategy : IFusionStrategy
{
    private readonly RagOptions _options;

    public RRFFusionStrategy(IOptions<RagOptions> options) => _options = options.Value;

    public List<FusedChunk> Fuse(
        List<RetrievedChunk> denseResults,
        List<RetrievedChunk> sparseResults,
        List<RetrievedChunk>? graphResults,
        int topK)
    {
        var k = _options.RRF_K;
        var scores = new Dictionary<string, (float rrf, float? dense, float? sparse, float? graph, DocumentChunk chunk)>();

        // Score dense results
        for (int i = 0; i < denseResults.Count; i++)
        {
            var chunk = denseResults[i].Chunk;
            var rrfScore = _options.VectorWeight * (1f / (k + i + 1));
            scores[chunk.ChunkId] = (rrfScore, denseResults[i].Score, null, null, chunk);
        }

        // Accumulate sparse results
        for (int i = 0; i < sparseResults.Count; i++)
        {
            var chunk = sparseResults[i].Chunk;
            var rrfScore = _options.BM25Weight * (1f / (k + i + 1));

            if (scores.TryGetValue(chunk.ChunkId, out var existing))
                scores[chunk.ChunkId] = (existing.rrf + rrfScore, existing.dense, sparseResults[i].Score, existing.graph, chunk);
            else
                scores[chunk.ChunkId] = (rrfScore, null, sparseResults[i].Score, null, chunk);
        }

        // Accumulate graph results (bonus weight for relationship-aware retrieval)
        if (graphResults is not null)
        {
            for (int i = 0; i < graphResults.Count; i++)
            {
                var chunk = graphResults[i].Chunk;
                var rrfScore = 0.3f * (1f / (k + i + 1));   // graph gets its own weight

                if (scores.TryGetValue(chunk.ChunkId, out var existing))
                    scores[chunk.ChunkId] = (existing.rrf + rrfScore, existing.dense, existing.sparse, graphResults[i].Score, chunk);
                else
                    scores[chunk.ChunkId] = (rrfScore, null, null, graphResults[i].Score, chunk);
            }
        }

        return scores.Values
            .OrderByDescending(s => s.rrf)
            .Take(topK)
            .Select(s => new FusedChunk(s.chunk, s.rrf, s.dense, s.sparse, s.graph))
            .ToList();
    }
}
