using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Models;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Infrastructure.Retrieval;
using Xunit;

namespace SmartDocQA.Infrastructure.Tests.Retrieval;

public class RRFFusionStrategyTests
{
    // Real production defaults from RagOptions — the test is a spec of the
    // actual config, not arbitrary numbers.
    private static RRFFusionStrategy CreateStrategy(
        float vectorWeight = 0.6f, float bm25Weight = 0.4f, int rrfK = 60)
    {
        var options = Options.Create(new RagOptions
        {
            VectorWeight = vectorWeight,
            BM25Weight = bm25Weight,
            RRF_K = rrfK
        });
        return new RRFFusionStrategy(options);
    }

    private static DocumentChunk MakeChunk(string chunkId) => new(
        ChunkId: chunkId,
        DocumentId: "doc-1",
        FileName: "test.pdf",
        Content: $"Content for {chunkId}",
        ChunkType: ChunkType.Text,
        PageNumber: 1,
        ChunkIndex: 0,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void Fuse_NonOverlappingResults_AppliesCorrectWeightedRRFScore()
    {
        // Arrange: chunk A found only by dense search, chunk B found only by sparse (BM25).
        // Both are rank 0 (top result) within their own list.
        var strategy = CreateStrategy(vectorWeight: 0.6f, bm25Weight: 0.4f, rrfK: 60);
        var chunkA = MakeChunk("chunk-A");
        var chunkB = MakeChunk("chunk-B");

        var dense = new List<RetrievedChunk> { new(chunkA, Score: 0.91f, RetrievalSource: "dense") };
        var sparse = new List<RetrievedChunk> { new(chunkB, Score: 5.2f, RetrievalSource: "sparse") };

        // Act
        var fused = strategy.Fuse(dense, sparse, graphResults: null, topK: 10);

        // Assert: RRF(d) = weight * 1 / (k + rank + 1), rank is 0-indexed so rank+1 = 1
        var expectedA = 0.6f * (1f / (60 + 1));   // ≈ 0.009836
        var expectedB = 0.4f * (1f / (60 + 1));   // ≈ 0.006557

        var resultA = fused.Single(f => f.Chunk.ChunkId == "chunk-A");
        var resultB = fused.Single(f => f.Chunk.ChunkId == "chunk-B");

        Assert.Equal(expectedA, resultA.FusedScore, precision: 5);
        Assert.Equal(expectedB, resultB.FusedScore, precision: 5);

        // Vector weight (0.6) beats BM25 weight (0.4) at equal rank, so A should
        // outrank B — this is the config's intent, not an accident.
        Assert.True(resultA.FusedScore > resultB.FusedScore);

        // Original per-source scores should be preserved for observability/debugging
        Assert.Equal(0.91f, resultA.DenseScore);
        Assert.Null(resultA.SparseScore);
        Assert.Equal(5.2f, resultB.SparseScore);
        Assert.Null(resultB.DenseScore);
    }

    [Fact]
    public void Fuse_SameChunkInDenseAndSparse_AccumulatesWeightedScores()
    {
        // Arrange: chunk C is found by BOTH dense and sparse at rank 0.
        // The implementation should ADD the two weighted contributions,
        // not just keep the higher one or overwrite it.
        var strategy = CreateStrategy(vectorWeight: 0.6f, bm25Weight: 0.4f, rrfK: 60);
        var chunkC = MakeChunk("chunk-C");

        var dense = new List<RetrievedChunk> { new(chunkC, Score: 0.88f, RetrievalSource: "dense") };
        var sparse = new List<RetrievedChunk> { new(chunkC, Score: 4.1f, RetrievalSource: "sparse") };

        // Act
        var fused = strategy.Fuse(dense, sparse, graphResults: null, topK: 10);

        // Assert: expected = (vectorWeight + bm25Weight) * 1/(k+1), since both
        // hit at rank 0 for the same chunk.
        var expected = (0.6f + 0.4f) * (1f / 61f);   // = 1.0 * (1/61) ≈ 0.016393

        var result = fused.Single(f => f.Chunk.ChunkId == "chunk-C");

        Assert.Equal(expected, result.FusedScore, precision: 5);

        // Both per-source scores should survive the merge, not just one
        Assert.Equal(0.88f, result.DenseScore);
        Assert.Equal(4.1f, result.SparseScore);

        // Single result overall — must not appear as two separate rows
        Assert.Single(fused);
    }

    [Fact]
    public void Fuse_ChunkFoundInGraphResults_AppliesFixedGraphWeight()
    {
        // Arrange: graph retrieval uses a fixed 0.3 weight regardless of
        // VectorWeight/BM25Weight config — this pins that hardcoded behavior
        // down so a future refactor can't silently change it.
        var strategy = CreateStrategy(vectorWeight: 0.6f, bm25Weight: 0.4f, rrfK: 60);
        var chunkD = MakeChunk("chunk-D");

        var graph = new List<RetrievedChunk> { new(chunkD, Score: 1.0f, RetrievalSource: "graph") };

        // Act: chunk only appears in graph results, nothing in dense/sparse
        var fused = strategy.Fuse(
            denseResults: new List<RetrievedChunk>(),
            sparseResults: new List<RetrievedChunk>(),
            graphResults: graph,
            topK: 10);

        // Assert
        var expected = 0.3f * (1f / 61f);   // ≈ 0.004918

        var result = fused.Single(f => f.Chunk.ChunkId == "chunk-D");
        Assert.Equal(expected, result.FusedScore, precision: 5);
        Assert.Equal(1.0f, result.GraphScore);
        Assert.Null(result.DenseScore);
        Assert.Null(result.SparseScore);
    }

    [Fact]
    public void Fuse_RespectsTopKLimit()
    {
        // Arrange: five distinct chunks, all found by dense search only.
        var strategy = CreateStrategy();
        var dense = Enumerable.Range(0, 5)
            .Select(i => new RetrievedChunk(MakeChunk($"chunk-{i}"), Score: 1.0f - i * 0.1f, RetrievalSource: "dense"))
            .ToList();

        // Act: ask for only the top 2
        var fused = strategy.Fuse(dense, new List<RetrievedChunk>(), null, topK: 2);

        // Assert
        Assert.Equal(2, fused.Count);
        // Rank 0 (chunk-0) must beat rank 1 (chunk-1) since lower rank index = higher RRF score
        Assert.Equal("chunk-0", fused[0].Chunk.ChunkId);
        Assert.Equal("chunk-1", fused[1].Chunk.ChunkId);
    }
}
