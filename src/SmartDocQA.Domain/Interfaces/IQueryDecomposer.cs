namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Decomposes a potentially compound question into one or more focused
/// sub-questions. A simple, single-fact question returns a single-item
/// list (itself, unchanged) -- decomposition only splits when the
/// question genuinely bundles multiple distinct asks together.
/// </summary>
public interface IQueryDecomposer
{
    Task<List<string>> DecomposeAsync(
        string question,
        int maxSubQuestions,
        CancellationToken ct = default);
}