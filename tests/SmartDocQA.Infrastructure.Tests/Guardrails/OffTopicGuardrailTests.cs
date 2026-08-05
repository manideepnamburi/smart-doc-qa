using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.Guardrails;
using Xunit;

namespace SmartDocQA.Infrastructure.Tests.Guardrails;

// ── Fakes, scoped to this test file's namespace only ────────────────────────
// (Deliberately not reusing DeleteDocumentsUseCaseTests' fakes -- different
// namespace, and this guardrail actually needs SearchAsync to return a real
// value, unlike the delete-flow fakes which never call it.)

public class FakeEmbeddingServiceForGuardrails : IEmbeddingService
{
    public int CallCount { get; private set; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new float[] { 0.1f, 0.2f, 0.3f }); // content doesn't matter -- the fake vector store ignores it
    }

    public Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default)
        => throw new NotImplementedException("OffTopicGuardrail only ever embeds one question at a time.");
}

public class FakeVectorStoreForGuardrails : IVectorStore
{
    // Test setup controls exactly what the top-1 search "finds" and at
    // what score, without needing a real Qdrant instance.
    public List<RetrievedChunk> ResultsToReturn { get; set; } = new();
    public int SearchCallCount { get; private set; }

    public Task<List<RetrievedChunk>> SearchAsync(float[] queryVector, int topK, string? documentIdFilter = null, CancellationToken ct = default)
    {
        SearchCallCount++;
        return Task.FromResult(ResultsToReturn);
    }

    public Task UpsertAsync(DocumentChunk chunk, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ResetCollectionAsync(CancellationToken ct = default) => throw new NotImplementedException();
}

public class OffTopicGuardrailTests
{
    private static DocumentChunk MakeChunk() => new(
        ChunkId: "chunk-1", DocumentId: "doc-1", FileName: "test.pdf",
        Content: "some content", ChunkType: ChunkType.Text,
        PageNumber: 1, ChunkIndex: 0, Metadata: new Dictionary<string, string>());

    private static (OffTopicGuardrail Guardrail, FakeEmbeddingServiceForGuardrails Embedder, FakeVectorStoreForGuardrails VectorStore)
        CreateSystemUnderTest(bool enabled, float threshold = 0.3f)
    {
        var embedder = new FakeEmbeddingServiceForGuardrails();
        var vectorStore = new FakeVectorStoreForGuardrails();
        var options = Options.Create(new GuardrailOptions
        {
            EnableOffTopicCheck = enabled,
            OffTopicSimilarityThreshold = threshold
        });
        var guardrail = new OffTopicGuardrail(embedder, vectorStore, options, NullLogger<OffTopicGuardrail>.Instance);
        return (guardrail, embedder, vectorStore);
    }

    [Fact]
    public async Task CheckAsync_Disabled_AlwaysPasses_AndNeverCallsEmbeddingOrVectorStore()
    {
        // Claim: when disabled, this guardrail must be a true zero-cost
        // no-op -- not just "returns Pass" but genuinely never makes the
        // embedding call or vector search at all.
        var (guardrail, embedder, vectorStore) = CreateSystemUnderTest(enabled: false);

        var result = await guardrail.CheckAsync("anything at all");

        Assert.True(result.Passed);
        Assert.Equal(0, embedder.CallCount);
        Assert.Equal(0, vectorStore.SearchCallCount);
    }

    [Fact]
    public async Task CheckAsync_TopScoreAboveThreshold_Passes()
    {
        var (guardrail, _, vectorStore) = CreateSystemUnderTest(enabled: true, threshold: 0.3f);
        vectorStore.ResultsToReturn = new List<RetrievedChunk>
        {
            new(MakeChunk(), Score: 0.85f, RetrievalSource: "dense")
        };

        var result = await guardrail.CheckAsync("What is the mission of NHANES?");

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_TopScoreBelowThreshold_Fails()
    {
        var (guardrail, _, vectorStore) = CreateSystemUnderTest(enabled: true, threshold: 0.3f);
        vectorStore.ResultsToReturn = new List<RetrievedChunk>
        {
            new(MakeChunk(), Score: 0.1f, RetrievalSource: "dense")
        };

        var result = await guardrail.CheckAsync("What's a good chocolate chip cookie recipe?");

        Assert.False(result.Passed);
        Assert.Contains("doesn't appear to relate", result.Reason);
    }

    [Fact]
    public async Task CheckAsync_NoSearchResultsAtAll_TreatedAsScoreZero_Fails()
    {
        // Claim: an empty result list (nothing in the collection resembles
        // the question at all) must be treated as score=0, not crash or
        // silently pass -- this is the "topScore = results.Count > 0 ? ... : 0f"
        // fallback in the real implementation.
        var (guardrail, _, vectorStore) = CreateSystemUnderTest(enabled: true, threshold: 0.3f);
        vectorStore.ResultsToReturn = new List<RetrievedChunk>(); // empty

        var result = await guardrail.CheckAsync("Completely unrelated question");

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_ScoreExactlyAtThreshold_Passes()
    {
        // Claim: the boundary condition -- the real code uses a strict
        // less-than check (topScore < threshold), so a score EQUAL to the
        // threshold must pass, not fail. Boundary conditions like this are
        // exactly where off-by-one bugs hide.
        var (guardrail, _, vectorStore) = CreateSystemUnderTest(enabled: true, threshold: 0.3f);
        vectorStore.ResultsToReturn = new List<RetrievedChunk>
        {
            new(MakeChunk(), Score: 0.3f, RetrievalSource: "dense") // exactly the threshold
        };

        var result = await guardrail.CheckAsync("borderline question");

        Assert.True(result.Passed);
    }
}
