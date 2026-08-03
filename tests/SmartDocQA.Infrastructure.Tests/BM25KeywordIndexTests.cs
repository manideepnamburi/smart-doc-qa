using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.Retrieval;
using Xunit;

namespace SmartDocQA.Infrastructure.Tests.Retrieval;

/// <summary>
/// Tests BM25KeywordIndex against a REAL, temporary SQLite database — not a
/// mock — because the class's own design (see its docstring) is specifically
/// about SQLite read/write performance and correct SQL, which a fake index
/// could never prove either way.
///
/// Isolation: xUnit creates a NEW instance of this test class for every
/// single test method. IAsyncLifetime's InitializeAsync runs before each
/// test and DisposeAsync runs after — so every test gets its own private,
/// empty database file, and that file is deleted afterward. No test can
/// ever see another test's leftover data.
/// </summary>
public class BM25KeywordIndexTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BM25KeywordIndex _index = null!;

    public Task InitializeAsync()
    {
        // A unique temp file per test instance. Path.GetTempPath() + a fresh
        // GUID guarantees no collision even if tests somehow ran in parallel.
        _dbPath = Path.Combine(Path.GetTempPath(), $"bm25_test_{Guid.NewGuid():N}.db");

        var options = Options.Create(new BM25Options { DatabasePath = _dbPath });
        _index = new BM25KeywordIndex(options, NullLogger<BM25KeywordIndex>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _index.DisposeAsync();

        // Microsoft.Data.Sqlite pools native connections under the hood for
        // performance, even after every C# SqliteConnection object has been
        // disposed. That pooled connection keeps an OS-level file handle
        // open, which blocks File.Delete below unless we explicitly release
        // the whole pool first.
        SqliteConnection.ClearAllPools();

        // SQLite can leave -wal (write-ahead log) and -shm (shared memory)
        // sidecar files alongside the main .db file. Clean up all three so
        // temp folders don't slowly fill up with leftover test artifacts.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Small helper so every test can build a chunk in one line instead of
    /// repeating the full 8-argument DocumentChunk constructor everywhere.
    /// </summary>
    /// <summary>
    /// Compares two scores by their actual numeric difference rather than by
    /// rounding both to N decimal places and comparing the rounded text.
    /// The latter (xUnit's Assert.Equal(x, y, precision:N)) can falsely fail
    /// when a hand-calculated value sits right on a rounding boundary — e.g.
    /// 0.2605 (hand calc, double precision) vs 0.260459 (actual, MathF's
    /// 32-bit float precision) are practically identical, but round to
    /// 0.261 vs 0.260 respectively at 3 decimals, which looks like a
    /// mismatch even though it isn't one.
    /// </summary>
    private static void AssertApproximately(float expected, float actual, float tolerance = 0.001f)
    {
        var difference = Math.Abs(expected - actual);
        Assert.True(difference <= tolerance,
            $"Expected approximately {expected} but got {actual} (difference {difference:F6} exceeds tolerance {tolerance}).");
    }

    private static DocumentChunk MakeChunk(string chunkId, string documentId, string content) => new(
        ChunkId: chunkId,
        DocumentId: documentId,
        FileName: $"{documentId}.pdf",
        Content: content,
        ChunkType: ChunkType.Text,
        PageNumber: 1,
        ChunkIndex: 0,
        Metadata: new Dictionary<string, string>());

    // ═══════════════════════════════════════════════════════════════════════
    // PART 1 — Bookkeeping correctness: did it remember and forget correctly?
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IndexAsync_ThenSearch_FindsTheIndexedChunk()
    {
        // Claim: if I hand the librarian a document, a search for a word it
        // contains actually finds it. The most basic "does this work at all."
        await _index.IndexAsync(MakeChunk("c1", "doc1", "diabetes affects blood sugar levels"));

        var results = await _index.SearchAsync("diabetes", topK: 10);

        Assert.Single(results);
        Assert.Equal("c1", results[0].Chunk.ChunkId);
    }

    [Fact]
    public async Task DeleteByDocumentAsync_RemovesChunksFromSubsequentSearches()
    {
        // Claim: "forget this document" actually works — a search performed
        // AFTER deletion must no longer find chunks from that document.
        await _index.IndexAsync(MakeChunk("c1", "doc1", "diabetes management guide"));
        await _index.IndexAsync(MakeChunk("c2", "doc2", "hypertension treatment guide"));

        await _index.DeleteByDocumentAsync("doc1");

        var diabetesResults = await _index.SearchAsync("diabetes", topK: 10);
        var hypertensionResults = await _index.SearchAsync("hypertension", topK: 10);

        Assert.Empty(diabetesResults);              // doc1's chunk is gone
        Assert.Single(hypertensionResults);          // doc2's chunk is untouched
    }

    [Fact]
    public async Task ClearAllAsync_EmptiesTheEntireIndex()
    {
        // Claim: "wipe everything" really means everything — used for full
        // system resets, so a partial wipe here would be a serious bug.
        await _index.IndexAsync(MakeChunk("c1", "doc1", "diabetes guide"));
        await _index.IndexAsync(MakeChunk("c2", "doc2", "hypertension guide"));

        await _index.ClearAllAsync();

        var results = await _index.SearchAsync("diabetes", topK: 10);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithDocumentIdFilter_ExcludesChunksFromOtherDocuments()
    {
        // Claim: scoping a search to "only within doc1" must exclude doc2's
        // chunks even when doc2 ALSO genuinely contains the search word —
        // this is what makes per-document Q&A ("ask only this PDF") work.
        await _index.IndexAsync(MakeChunk("c1", "doc1", "diabetes management in doc1"));
        await _index.IndexAsync(MakeChunk("c2", "doc2", "diabetes management in doc2"));

        var results = await _index.SearchAsync("diabetes", topK: 10, documentIdFilter: "doc1");

        Assert.Single(results);
        Assert.Equal("doc1", results[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task SearchAsync_RespectsTopKLimit_ReturningHighestScoredFirst()
    {
        // Claim: "give me the top 3" must return the 3 BEST matches, not just
        // any 3 out of many. We index 5 chunks with a deliberately increasing
        // number of mentions of "obesity" (1, 2, 3, 4, 5 times), so the
        // highest chunk index should always win the top spot.
        for (int i = 1; i <= 5; i++)
        {
            var mentions = string.Join(" ", Enumerable.Repeat("obesity", i));
            await _index.IndexAsync(MakeChunk($"c{i}", "doc1", $"{mentions} filler words here"));
        }

        var results = await _index.SearchAsync("obesity", topK: 3);

        Assert.Equal(3, results.Count);
        // c5 (5 mentions) must outrank c4 (4 mentions) must outrank c3 (3 mentions)
        Assert.Equal("c5", results[0].Chunk.ChunkId);
        Assert.Equal("c4", results[1].Chunk.ChunkId);
        Assert.Equal("c3", results[2].Chunk.ChunkId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PART 2 — Ranking intelligence: hand-calculated proofs of the BM25 math
    //
    // K1=1.5, B=0.75 are the real constants from BM25KeywordIndex.cs.
    // Formula: score = idf * (tf*(K1+1)) / (tf + K1*(1-B+B*(docLen/avgDocLen)))
    //          idf   = ln(((n-df+0.5)/(df+0.5)) + 1)
    // Every expected number below was computed by hand with this exact
    // formula BEFORE running the test — the test proves the code matches
    // the math, not the other way around.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchAsync_RareTermScoresMuchHigherThanCommonTerm_SameOccurrenceCount()
    {
        // Claim (the "diabetes vs blood" question): a word that appears in
        // only 1 of 10 chunks ("diabetes") must score FAR higher than a word
        // appearing in all 10 chunks ("blood") — even when both appear
        // exactly once in the chunk being scored, and all chunks are the
        // same length (so length-normalization can't be the explanation —
        // this isolates rarity as the only variable).
        //
        // Hand calculation, n=10 chunks, all length=5 words:
        //   idf(diabetes), df=1:  ln(((10-1+0.5)/(1+0.5))+1) = ln(7.333) = 1.9924
        //   idf(blood),    df=10: ln(((10-10+0.5)/(10+0.5))+1) = ln(1.0476) = 0.0465
        //   TF component is identical for both (tf=1, docLen=avgDocLen=5):
        //     (1*(1.5+1)) / (1+1.5*(1-0.75+0.75*1)) = 2.5/2.5 = 1.0
        //   score(diabetes) = 1.9924 * 1.0 = 1.9924
        //   score(blood)    = 0.0465 * 1.0 = 0.0465
        //   -> diabetes should score ~42.8x higher than blood, despite an
        //      identical single occurrence in the same chunk.

        await _index.IndexAsync(MakeChunk("c1", "doc1", "diabetes blood alpha beta gamma"));
        for (int i = 2; i <= 10; i++)
            await _index.IndexAsync(MakeChunk($"c{i}", "doc1", $"blood w{i}a w{i}b w{i}c w{i}d"));

        var diabetesResults = await _index.SearchAsync("diabetes", topK: 10);
        var bloodResultsForC1 = (await _index.SearchAsync("blood", topK: 10))
            .Single(r => r.Chunk.ChunkId == "c1");

        Assert.Single(diabetesResults);
        Assert.Equal("c1", diabetesResults[0].Chunk.ChunkId);

        AssertApproximately(1.9924f, diabetesResults[0].Score);
        AssertApproximately(0.0465f, bloodResultsForC1.Score);

        // The real point of this test, stated as a ratio rather than exact
        // numbers, so it stays meaningful even if the formula's constants
        // ever change slightly:
        Assert.True(diabetesResults[0].Score > bloodResultsForC1.Score * 30,
            "A word in only 1/10 chunks should score dramatically higher than a word in 10/10 chunks.");
    }

    [Fact]
    public async Task SearchAsync_UbiquitousWordContributesNearZeroScore_EvenWhenRepeatedOften()
    {
        // Claim (the "the/and/as" question, and the safety net for the
        // no-stopword-filtering Known Issue): a word appearing in EVERY
        // chunk should contribute almost nothing to the score, even if it's
        // repeated heavily within a chunk — proving the system doesn't need
        // an explicit stopword list because the math self-neutralizes them.
        //
        // Hand calculation, n=10 chunks, "the" appears 4 times in every
        // 8-word chunk (df=10, n=10 -> same idf=0.0465 as "blood" above):
        //   tf=4, docLen=avgDocLen=8 (ratio=1):
        //     (4*2.5) / (4+1.5*1) = 10/5.5 = 1.8182
        //   score = 0.0465 * 1.8182 = 0.0846
        //   -> stays small (<0.15) despite "the" being HALF the chunk's words.

        for (int i = 1; i <= 10; i++)
            await _index.IndexAsync(MakeChunk($"c{i}", "doc1", $"the the the the unique{i}a unique{i}b unique{i}c unique{i}d"));

        var results = await _index.SearchAsync("the", topK: 1);

        Assert.Single(results);
        AssertApproximately(0.0846f, results[0].Score);
        Assert.True(results[0].Score < 0.15f,
            "A word present in every chunk should never contribute a meaningful score, even when repeated.");
    }

    [Fact]
    public async Task SearchAsync_RepeatedMentions_GiveDiminishingReturnsNotLinearScaling()
    {
        // Claim: a chunk mentioning a term 20 times should NOT score ~20x
        // higher than one mentioning it 2 times — that would reward keyword
        // stuffing. BM25's K1 constant caps the benefit of repetition.
        //
        // Hand calculation, n=2 chunks, both length=20 words (so avgDocLen=20
        // and length-normalization is identical for both — isolates TF alone):
        //   df("obesity")=2, n=2: idf = ln(((2-2+0.5)/(2+0.5))+1) = ln(1.2) = 0.1823
        //   ChunkA, tf=2:  (2*2.5)/(2+1.5*1) = 5/3.5 = 1.4286 -> score = 0.2605
        //   ChunkB, tf=20: (20*2.5)/(20+1.5) = 50/21.5 = 2.3256 -> score = 0.4240
        //   -> tf went up 10x (2->20) but score only went up ~1.63x (0.26->0.42),
        //      proving strong diminishing returns, not proportional scaling.

        var chunkAWords = string.Join(" ", Enumerable.Repeat("obesity", 2)
            .Concat(Enumerable.Range(1, 18).Select(i => $"filler{i}")));
        var chunkBWords = string.Join(" ", Enumerable.Repeat("obesity", 20));

        await _index.IndexAsync(MakeChunk("chunkA", "doc1", chunkAWords));  // 2 mentions, 20 words total
        await _index.IndexAsync(MakeChunk("chunkB", "doc1", chunkBWords)); // 20 mentions, 20 words total

        var results = await _index.SearchAsync("obesity", topK: 2);
        var scoreA = results.Single(r => r.Chunk.ChunkId == "chunkA").Score;
        var scoreB = results.Single(r => r.Chunk.ChunkId == "chunkB").Score;

        AssertApproximately(0.2605f, scoreA);
        AssertApproximately(0.4240f, scoreB);

        // The real point: 10x more repetition must NOT produce anywhere
        // close to 10x more score. We assert it stays under 2x as the
        // "diminishing returns actually happened" check.
        Assert.True(scoreB < scoreA * 2,
            "10x more term repetition should not come close to 10x more score (K1 saturation).");
    }

    [Fact]
    public async Task SearchAsync_ShorterFocusedChunk_ScoresHigherThanLongerChunk_SameTermCount()
    {
        // Claim: a short, focused chunk should score higher than a long,
        // sprawling chunk mentioning the same term the same number of times
        // — a 2-mention hit in a 10-word chunk is more "about" the topic
        // than a 2-mention hit in a 40-word chunk.
        //
        // Hand calculation, n=2 chunks (avgDocLen=(10+40)/2=25):
        //   df("cholesterol")=2, n=2: idf = ln(1.2) = 0.1823 (same as test above)
        //   ChunkShort, tf=2, docLen=10 (ratio=10/25=0.4):
        //     denom = 2+1.5*(0.25+0.75*0.4) = 2+1.5*0.55 = 2.825
        //     component = 5/2.825 = 1.7699 -> score = 0.3227
        //   ChunkLong, tf=2, docLen=40 (ratio=40/25=1.6):
        //     denom = 2+1.5*(0.25+0.75*1.6) = 2+1.5*1.45 = 4.175
        //     component = 5/4.175 = 1.1976 -> score = 0.2183
        //   -> same tf=2 in both, but the short chunk scores meaningfully
        //      higher (0.3227 vs 0.2183) purely from length normalization.

        var shortChunkWords = string.Join(" ",
            Enumerable.Repeat("cholesterol", 2).Concat(Enumerable.Range(1, 8).Select(i => $"s{i}")));
        var longChunkWords = string.Join(" ",
            Enumerable.Repeat("cholesterol", 2).Concat(Enumerable.Range(1, 38).Select(i => $"l{i}")));

        await _index.IndexAsync(MakeChunk("chunkShort", "doc1", shortChunkWords)); // 10 words total
        await _index.IndexAsync(MakeChunk("chunkLong", "doc1", longChunkWords));   // 40 words total

        var results = await _index.SearchAsync("cholesterol", topK: 2);
        var scoreShort = results.Single(r => r.Chunk.ChunkId == "chunkShort").Score;
        var scoreLong = results.Single(r => r.Chunk.ChunkId == "chunkLong").Score;

        AssertApproximately(0.3227f, scoreShort);
        AssertApproximately(0.2183f, scoreLong);
        Assert.True(scoreShort > scoreLong,
            "Same term count in a shorter chunk should score higher than in a longer, more diluted chunk.");
    }
}
