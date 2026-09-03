namespace SmartDocQA.Domain.Interfaces;

public interface ILlmClient
{
    /// <summary>
    /// modelOverride: if provided, uses this exact model string instead of
    /// the configured AnthropicOptions.ChatModel for this one call. Lets a
    /// caller (e.g. a cheap safety-classifier guardrail) deliberately use a
    /// faster/cheaper model than whatever the main synthesis pipeline uses,
    /// without needing a second injected client or config section.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        CancellationToken ct = default, string? modelOverride = null);

    Task<string> CompleteWithVisionAsync(string systemPrompt, string userPrompt,
        byte[] imageBytes, CancellationToken ct = default);
}
