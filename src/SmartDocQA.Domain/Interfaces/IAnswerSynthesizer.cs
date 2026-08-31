using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Builds the final answer from ranked chunks + query. Extracts citations.
/// </summary>
public interface IAnswerSynthesizer
{
    Task<QAResult> SynthesizeAsync(QAQuery query, List<RankedChunk> rankedChunks, CancellationToken ct = default);
}