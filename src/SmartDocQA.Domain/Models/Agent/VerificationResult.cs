namespace SmartDocQA.Domain.Models;

/// <summary>
/// Output of IAnswerVerifier.VerifyAsync — whether the retrieved evidence
/// for a sub-question actually answers it, plus a human-readable reason
/// either way (used both for logging when verified, and to inform the
/// query reformulation step about what specifically was missing when it
/// isn't).
/// </summary>
public record VerificationResult(bool Verified, string Reason);