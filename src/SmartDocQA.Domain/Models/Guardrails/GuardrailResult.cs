namespace SmartDocQA.Domain.Models;

/// <summary>
/// Result of a single guardrail check. Passed=false means the question
/// (for input guardrails) or answer (for output guardrails) failed this
/// specific check, with Reason explaining why in human-readable form.
/// </summary>
public record GuardrailResult(bool Passed, string? Reason = null)
{
    /// <summary>Convenience factory — most checks either pass cleanly or fail with a reason.</summary>
    public static GuardrailResult Pass() => new(true, null);

    /// <summary>Convenience factory — most checks either pass cleanly or fail with a reason.</summary>
    public static GuardrailResult Fail(string reason) => new(false, reason);
}
