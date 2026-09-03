using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Checks the raw incoming question BEFORE any retrieval/synthesis work
/// happens — this ordering matters for cost: a rejected question never
/// triggers an embedding call, retrieval, or a Claude synthesis call.
/// Multiple implementations can be registered (prompt injection, PII scrub,
/// off-topic detection); QueryDocumentUseCase runs all of them.
/// </summary>
public interface IInputGuardrail
{
    /// <summary>
    /// fallbackToLLM: mirrors QAQuery.FallbackToLLM. When true, the caller
    /// has explicitly opted into general-knowledge answers -- guardrails
    /// that reject purely for being "off topic relative to the documents"
    /// should respect that and pass the question through. Guardrails
    /// checking for genuine safety concerns (prompt injection, PII,
    /// harmful requests) must ignore this flag entirely and keep blocking
    /// regardless -- this is a scope preference, not a safety override.
    /// </summary>
    Task<GuardrailResult> CheckAsync(string question, bool fallbackToLLM = false, CancellationToken ct = default);
}
