using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

/// <summary>
/// Output of IRetrievalPipeline.RetrieveAndRankAsync — the final ranked
/// chunks ready for answer synthesis, plus the metadata (RetrievalMode,
/// chunk counts) that QueryDocumentUseCase's response already reports
/// today. Keeping these together in one result means the pipeline is the
/// single source of truth for both the chunks AND the metadata describing
/// how they were retrieved.
/// </summary>
public record RetrievalPipelineResult(
    List<RankedChunk> RankedChunks,
    RetrievalMode Mode,
    int ChunksRetrieved,
    int ChunksAfterRerank);
