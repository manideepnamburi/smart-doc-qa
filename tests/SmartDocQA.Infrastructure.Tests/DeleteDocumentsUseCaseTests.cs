using Microsoft.Extensions.Logging.Abstractions;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using Xunit;

namespace SmartDocQA.Application.Tests.UseCases;

// ═════════════════════════════════════════════════════════════════════════
// FAKE STAND-INS FOR THE FOUR STORE INTERFACES
//
// DeleteDocumentsUseCase's entire job is DECIDING which store methods to
// call, on which documents, in what order/circumstances — it never does any
// real storage work itself. So testing it doesn't need a real Qdrant/SQLite/
// Neo4j — it needs something that RECORDS what was called on it, so a test
// can assert "was DeleteByDocumentAsync called for doc-123? was it called
// exactly once? was ResetCollectionAsync called instead of a per-document
// loop?" That's what each Fake* class below does.
//
// Each fake implements the full interface (required by the compiler) but
// only meaningfully implements the methods DeleteDocumentsUseCase actually
// calls. The rest throw NotImplementedException with a clear message — if
// a test ever hits one of those, it's a signal that either the production
// code changed to call something new, or the test itself made a mistake.
// ═════════════════════════════════════════════════════════════════════════

public class FakeDocumentRegistry : IDocumentRegistry
{
    // Test setup pre-loads this with whatever documents "already exist"
    // for a given test scenario.
    public List<RegistryEntry> Entries { get; set; } = new();

    // What tests assert on afterward: which document IDs got unregistered,
    // whether a full clear happened, and — for ingest-side tests — every
    // RegisterAsync call that was made.
    public List<string> UnregisteredDocumentIds { get; } = new();
    public bool ClearAllWasCalled { get; private set; }
    public List<RegistryEntry> RegisteredCalls { get; } = new();

    public Task<List<RegistryEntry>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(Entries);

    public Task UnregisterAsync(string documentId, CancellationToken ct = default)
    {
        UnregisteredDocumentIds.Add(documentId);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        ClearAllWasCalled = true;
        return Task.CompletedTask;
    }

    // Ingest-side methods — genuinely implemented (not throwing) so this
    // one fake works for both DeleteDocumentsUseCaseTests and
    // IngestFolderUseCaseTests without duplicating a second fake class.
    public Task<bool> IsIngestedAsync(string filePath, CancellationToken ct = default)
    {
        var isIngested = Entries.Any(e =>
            string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(isIngested);
    }

    public Task RegisterAsync(string filePath, string documentId, string fileName,
        long fileSizeBytes, CancellationToken ct = default)
    {
        var entry = new RegistryEntry(filePath, documentId, fileName, fileSizeBytes, DateTime.UtcNow);
        RegisteredCalls.Add(entry);
        Entries.Add(entry); // so a subsequent IsIngestedAsync check reflects the new registration
        return Task.CompletedTask;
    }
}

public class FakeVectorStore : IVectorStore
{
    public List<string> DeletedDocumentIds { get; } = new();
    public bool ResetCollectionWasCalled { get; private set; }

    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        DeletedDocumentIds.Add(documentId);
        return Task.CompletedTask;
    }

    public Task ResetCollectionAsync(CancellationToken ct = default)
    {
        ResetCollectionWasCalled = true;
        return Task.CompletedTask;
    }

    // Not used by the delete flow (ingest/search-side concerns).
    public Task UpsertAsync(DocumentChunk chunk, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpsertBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<List<RetrievedChunk>> SearchAsync(float[] queryVector, int topK, string? documentIdFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}

public class FakeKeywordIndex : IKeywordIndex
{
    public List<string> DeletedDocumentIds { get; } = new();
    public bool ClearAllWasCalled { get; private set; }

    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        DeletedDocumentIds.Add(documentId);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        ClearAllWasCalled = true;
        return Task.CompletedTask;
    }

    // Not used by the delete flow.
    public Task IndexAsync(DocumentChunk chunk, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task IndexBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<List<RetrievedChunk>> SearchAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}

public class FakeGraphStore : IGraphStore
{
    public List<string> DeletedDocumentIds { get; } = new();

    // Lets a test say "throw an exception when document X's graph cleanup
    // is attempted" — this is how we prove the non-fatal try/catch behavior
    // actually works, without needing a real Neo4j failure to happen.
    public HashSet<string> DocumentIdsThatShouldThrow { get; set; } = new();

    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        DeletedDocumentIds.Add(documentId);
        if (DocumentIdsThatShouldThrow.Contains(documentId))
            throw new InvalidOperationException($"Simulated Neo4j failure for {documentId}");
        return Task.CompletedTask;
    }

    // Not used by the delete flow (ingest-side concerns).
    public Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpsertRelationshipAsync(GraphRelationship relationship, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<List<RetrievedChunk>> SearchByEntityAsync(string query, int topK, CancellationToken ct = default)
        => throw new NotImplementedException();
}

// ═════════════════════════════════════════════════════════════════════════
// THE ACTUAL TESTS
// ═════════════════════════════════════════════════════════════════════════

public class DeleteDocumentsUseCaseTests
{
    // Every test builds its own fresh set of fakes and the real use case
    // wired against them — no shared/static state between tests, so tests
    // can never accidentally see each other's recorded calls.
    private static (DeleteDocumentsUseCase UseCase, FakeDocumentRegistry Registry,
        FakeVectorStore VectorStore, FakeKeywordIndex KeywordIndex, FakeGraphStore GraphStore)
        CreateSystemUnderTest()
    {
        var registry = new FakeDocumentRegistry();
        var vectorStore = new FakeVectorStore();
        var keywordIndex = new FakeKeywordIndex();
        var graphStore = new FakeGraphStore();

        var useCase = new DeleteDocumentsUseCase(
            registry, vectorStore, keywordIndex, graphStore,
            NullLogger<DeleteDocumentsUseCase>.Instance);

        return (useCase, registry, vectorStore, keywordIndex, graphStore);
    }

    private static RegistryEntry MakeEntry(string fileName, string documentId) => new(
        FilePath: $"C:/docs/{fileName}",
        DocumentId: documentId,
        FileName: fileName,
        FileSizeBytes: 1024,
        IngestedAt: DateTime.UtcNow);

    // ── Priority ordering ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FilesToDeleteProvided_TakesPriorityOverFileTypesAndDeleteAll()
    {
        // Claim: even if FileTypes AND DeleteAll are ALSO set on the same
        // request, FilesToDelete (Priority 1) must win and the other two
        // criteria must be completely ignored.
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("FileA.pdf", "doc-1") };

        var request = new DeleteRequest(
            FilesToDelete: new List<string> { "FileA.pdf" },
            FileTypes: new List<string> { "docx" },  // should be ignored
            DeleteAll: true);                         // should be ignored

        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(DeleteMode.SpecificFiles, result.Mode);
        Assert.Single(result.Deleted);
    }

    [Fact]
    public async Task ExecuteAsync_FileTypesProvided_WhenFilesToDeleteEmpty_UsesFileTypesMode()
    {
        // Claim: Priority 2 kicks in only when Priority 1's list is empty.
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("Report.docx", "doc-1") };

        var request = new DeleteRequest(
            FilesToDelete: new List<string>(),  // empty -> falls through
            FileTypes: new List<string> { "docx" },
            DeleteAll: true);                    // should still be ignored

        var result = await useCase.ExecuteAsync(request);

        Assert.Equal(DeleteMode.FileTypes, result.Mode);
    }

    [Fact]
    public async Task ExecuteAsync_NothingProvided_ReturnsValidationError_AndTouchesNoStoreAtAll()
    {
        // Claim: an empty request is a caller mistake, not "delete
        // everything" or "delete nothing" — it must return an explicit
        // error, AND it must not call any store's delete/clear method,
        // proving the guard clause returns before any store is touched.
        var (useCase, registry, vectorStore, keywordIndex, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("FileA.pdf", "doc-1") };

        var request = new DeleteRequest(
            FilesToDelete: new List<string>(),
            FileTypes: new List<string>(),
            DeleteAll: false);

        var result = await useCase.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Equal(DeleteMode.None, result.Mode);
        Assert.NotNull(result.Error);

        // The real proof: zero calls reached any of the four stores.
        Assert.Empty(vectorStore.DeletedDocumentIds);
        Assert.Empty(keywordIndex.DeletedDocumentIds);
        Assert.Empty(graphStore.DeletedDocumentIds);
        Assert.Empty(registry.UnregisteredDocumentIds);
        Assert.False(registry.ClearAllWasCalled);
    }

    // ── SpecificFiles mode ───────────────────────────────────────────────

    [Fact]
    public async Task DeleteByFileNames_FileNotInRegistry_ReportedAsNotFound_NotTreatedAsError()
    {
        // Claim: a mistyped/missing filename is reported back to the
        // caller, but it does NOT make the overall request fail — other
        // valid files in the same request still get deleted.
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("FileA.pdf", "doc-1") };

        var request = new DeleteRequest(
            FilesToDelete: new List<string> { "FileA.pdf", "TypoFile.pdf" },
            FileTypes: null,
            DeleteAll: false);

        var result = await useCase.ExecuteAsync(request);

        Assert.True(result.Success);
        Assert.Single(result.Deleted);
        Assert.Single(result.NotFound);
        Assert.Equal("TypoFile.pdf", result.NotFound[0]);
    }

    [Fact]
    public async Task DeleteByFileNames_MatchedFile_CallsAllFourStoresForThatDocument()
    {
        // Claim: the core "four stores" promise from the class docstring —
        // deleting one document must reach Qdrant, BM25, Neo4j, AND the
        // registry, all for that document's exact ID.
        var (useCase, registry, vectorStore, keywordIndex, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("FileA.pdf", "doc-1") };

        var request = new DeleteRequest(FilesToDelete: new List<string> { "FileA.pdf" }, FileTypes: null, DeleteAll: false);
        await useCase.ExecuteAsync(request);

        Assert.Contains("doc-1", vectorStore.DeletedDocumentIds);
        Assert.Contains("doc-1", keywordIndex.DeletedDocumentIds);
        Assert.Contains("doc-1", graphStore.DeletedDocumentIds);
        Assert.Contains("doc-1", registry.UnregisteredDocumentIds);
    }

    [Fact]
    public async Task DeleteByFileNames_ExtensionSensitive_DoesNotConfuseSameNameDifferentExtension()
    {
        // Claim from the docstring: "FileA.pdf" and "FileA.txt" are
        // different documents. Requesting deletion of one must never
        // touch the other, even though they share a base name.
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("FileA.pdf", "doc-pdf"),
            MakeEntry("FileA.txt", "doc-txt")
        };

        var request = new DeleteRequest(FilesToDelete: new List<string> { "FileA.pdf" }, FileTypes: null, DeleteAll: false);
        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Deleted);
        Assert.Equal("doc-pdf", result.Deleted[0].DocumentId);
    }

    [Fact]
    public async Task DeleteByFileNames_GraphStoreThrows_DeletionStillCompletes_AndLoopContinuesToNextFile()
    {
        // Claim: this is the non-fatal-graph-failure guarantee. If Neo4j
        // cleanup fails for doc-1, (a) doc-1's OTHER three stores still get
        // cleaned up and it's still reported as Deleted, and (b) the loop
        // does not abort — doc-2 (requested in the same call) still gets
        // fully processed too.
        var (useCase, registry, vectorStore, keywordIndex, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("FileA.pdf", "doc-1"),
            MakeEntry("FileB.pdf", "doc-2")
        };
        graphStore.DocumentIdsThatShouldThrow = new HashSet<string> { "doc-1" };

        var request = new DeleteRequest(
            FilesToDelete: new List<string> { "FileA.pdf", "FileB.pdf" }, FileTypes: null, DeleteAll: false);
        var result = await useCase.ExecuteAsync(request);

        // Both files still end up fully deleted despite doc-1's graph failure.
        Assert.Equal(2, result.Deleted.Count);
        Assert.Contains("doc-1", vectorStore.DeletedDocumentIds);
        Assert.Contains("doc-1", registry.UnregisteredDocumentIds);
        // doc-2, unaffected by doc-1's failure, also completed normally.
        Assert.Contains("doc-2", vectorStore.DeletedDocumentIds);
        Assert.Contains("doc-2", graphStore.DeletedDocumentIds);
    }

    // ── FileTypes mode ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteByFileTypes_AcceptsExtensionWithOrWithoutLeadingDot_CaseInsensitive()
    {
        // Claim: callers can pass "pdf" or ".pdf" or "PDF" and get the same
        // result — normalization happens before matching.
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("Report.PDF", "doc-1"),   // uppercase extension on disk
            MakeEntry("Notes.docx", "doc-2")
        };

        var request = new DeleteRequest(
            FilesToDelete: null,
            FileTypes: new List<string> { "pdf" }, // no leading dot, lowercase
            DeleteAll: false);

        var result = await useCase.ExecuteAsync(request);

        Assert.Single(result.Deleted);
        Assert.Equal("doc-1", result.Deleted[0].DocumentId);
    }

    [Fact]
    public async Task DeleteByFileTypes_NoMatches_ReturnsSuccessTrue_NotAnError()
    {
        // Claim: "no files of this type exist" is a valid, successful
        // outcome (zero deletions), not a failure — a caller shouldn't
        // have to distinguish "worked, found nothing" from "broke."
        var (useCase, registry, _, _, _) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry> { MakeEntry("Report.pdf", "doc-1") };

        var request = new DeleteRequest(
            FilesToDelete: null,
            FileTypes: new List<string> { "xlsx" }, // nothing matches
            DeleteAll: false);

        var result = await useCase.ExecuteAsync(request);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Empty(result.Deleted);
    }

    // ── DeleteAll mode ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAll_UsesBulkClearMethods_NeverLoopsPerDocumentForThoseThreeStores()
    {
        // Claim from the docstring: Qdrant/BM25/Registry get ONE bulk clear
        // call each — NOT a per-document loop like SpecificFiles/FileTypes
        // use. This proves DeleteAll takes the fast bulk path, not a slow
        // per-document path, for the three stores that support bulk clear.
        var (useCase, registry, vectorStore, keywordIndex, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("FileA.pdf", "doc-1"),
            MakeEntry("FileB.pdf", "doc-2"),
            MakeEntry("FileC.pdf", "doc-3")
        };

        var request = new DeleteRequest(FilesToDelete: null, FileTypes: null, DeleteAll: true);
        await useCase.ExecuteAsync(request);

        Assert.True(vectorStore.ResetCollectionWasCalled);
        Assert.True(keywordIndex.ClearAllWasCalled);
        Assert.True(registry.ClearAllWasCalled);

        // The negative assertion is the real proof: per-document delete
        // was NEVER called on these three stores during DeleteAll.
        Assert.Empty(vectorStore.DeletedDocumentIds);
        Assert.Empty(keywordIndex.DeletedDocumentIds);
    }

    [Fact]
    public async Task DeleteAll_StillLoopsGraphStorePerDocument_BecauseNoGraphBulkWipeExists()
    {
        // Claim from the docstring: unlike the other three, the graph store
        // has no bulk-wipe method, so DeleteAll must call
        // DeleteByDocumentAsync once per document — exactly 3 times here,
        // once per registered document.
        var (useCase, registry, _, _, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("FileA.pdf", "doc-1"),
            MakeEntry("FileB.pdf", "doc-2"),
            MakeEntry("FileC.pdf", "doc-3")
        };

        await useCase.ExecuteAsync(new DeleteRequest(FilesToDelete: null, FileTypes: null, DeleteAll: true));

        Assert.Equal(3, graphStore.DeletedDocumentIds.Count);
        Assert.Contains("doc-1", graphStore.DeletedDocumentIds);
        Assert.Contains("doc-2", graphStore.DeletedDocumentIds);
        Assert.Contains("doc-3", graphStore.DeletedDocumentIds);
    }

    [Fact]
    public async Task DeleteAll_OneDocumentsGraphFailure_DoesNotStopGraphCleanupForTheOthers()
    {
        // Claim: same non-fatal-per-document principle as the SpecificFiles
        // path, but proven here for DeleteAll's own separate loop — one
        // document's Neo4j failure must not prevent the other two
        // documents' graph cleanup from being attempted.
        var (useCase, registry, _, _, graphStore) = CreateSystemUnderTest();
        registry.Entries = new List<RegistryEntry>
        {
            MakeEntry("FileA.pdf", "doc-1"),
            MakeEntry("FileB.pdf", "doc-2"),
            MakeEntry("FileC.pdf", "doc-3")
        };
        graphStore.DocumentIdsThatShouldThrow = new HashSet<string> { "doc-2" };

        var result = await useCase.ExecuteAsync(new DeleteRequest(FilesToDelete: null, FileTypes: null, DeleteAll: true));

        // The overall DeleteAll operation still reports success...
        Assert.True(result.Success);
        Assert.Equal(3, result.Deleted.Count);
        // ...and the graph store was still ASKED to clean up all three,
        // even though doc-2's attempt threw.
        Assert.Equal(3, graphStore.DeletedDocumentIds.Count);
    }
}
