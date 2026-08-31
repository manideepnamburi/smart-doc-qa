using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Checks the synthesized answer AFTER the full pipeline has run, before
/// it's returned to the caller — the last line of defense against an
/// answer that's factually ungrounded, inappropriate, or otherwise unsafe
/// to return as-is.
/// </summary>
public interface IOutputGuardrail
{
    Task<GuardrailResult> CheckAsync(QAResult result, CancellationToken ct = default);
}