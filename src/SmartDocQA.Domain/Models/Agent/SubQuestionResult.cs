namespace SmartDocQA.Domain.Models;

/// <summary>
/// One sub-question's full lifecycle through the agent pipeline: the
/// question text itself, its retrieved+ranked evidence, whether the
/// verifier accepted it, and how many attempts it took.
///
/// Uses List&lt;RankedChunk&gt;, not List&lt;RetrievedChunk&gt;, because
/// IRetrievalPipeline.RetrieveAndRankAsync returns RankedChunk -- the
/// reranked, scored result ready for synthesis. RankedChunk carries the
/// reranker score needed to judge evidence quality per sub-question;
/// RetrievedChunk is the earlier, pre-fusion/pre-rerank shape and isn't
/// what synthesis should consume.
/// </summary>
public record SubQuestionResult(
    string Question,
    List<RankedChunk> RankedChunks,
    bool Verified,
    string? VerificationReason,
    int AttemptsUsed);