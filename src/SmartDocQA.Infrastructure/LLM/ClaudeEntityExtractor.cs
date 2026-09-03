using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using System.Text.Json;

namespace SmartDocQA.Infrastructure;

// ─── Claude Entity Extractor (Phase 5) ───────────────────────────────────────
//
// Reads a chunk's text content and asks Claude to extract entities +
// relationships as structured JSON, for the knowledge graph.
// Applies to ALL chunk types (Text, Table, Chart) — numeric/relational
// facts often live in tables and chart descriptions, not just prose.

public class ClaudeEntityExtractor : IEntityExtractor
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly ILogger<ClaudeEntityExtractor> _logger;

    private const string SystemPrompt =
        "You are a precise, conservative entity-relationship extraction " +
        "system. You only output valid JSON, never prose.";

    public ClaudeEntityExtractor(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        ILogger<ClaudeEntityExtractor> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _logger = logger;
    }

    public async Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default)
    {
        // Skip near-empty chunks — nothing worth extracting
        if (string.IsNullOrWhiteSpace(chunk.Content) || chunk.Content.Length < 20)
            return (new List<GraphEntity>(), new List<GraphRelationship>());

        var userPrompt = _promptLoader.LoadAndFill("entity_extraction.txt",
            new Dictionary<string, string> { ["content"] = chunk.Content });

        string response;
        try
        {
            response = await _llmClient.CompleteAsync(SystemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Entity extraction call failed for chunk {ChunkId} — skipping",
                chunk.ChunkId);
            return (new List<GraphEntity>(), new List<GraphRelationship>());
        }

        return ParseResponse(response, chunk);
    }

    private (List<GraphEntity>, List<GraphRelationship>) ParseResponse(
        string response, DocumentChunk chunk)
    {
        try
        {
            // Claude sometimes wraps JSON in ```json fences despite instructions —
            // strip defensively
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("```", "")
                    .Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);

            var entities = doc.RootElement.GetProperty("entities")
                .EnumerateArray()
                .Select(e => new GraphEntity(
                    Name: e.GetProperty("name").GetString() ?? "",
                    Type: e.GetProperty("type").GetString() ?? "",
                    DocumentId: chunk.DocumentId,
                    PageNumber: chunk.PageNumber))
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .ToList();

            var relationships = doc.RootElement.GetProperty("relationships")
                .EnumerateArray()
                .Select(r => new GraphRelationship(
                    FromEntity: r.GetProperty("from").GetString() ?? "",
                    ToEntity: r.GetProperty("to").GetString() ?? "",
                    RelationType: r.GetProperty("type").GetString() ?? "",
                    DocumentId: chunk.DocumentId))
                .Where(r => !string.IsNullOrWhiteSpace(r.FromEntity)
                         && !string.IsNullOrWhiteSpace(r.ToEntity))
                .ToList();

            _logger.LogInformation(
                "Chunk {ChunkId}: extracted {EntityCount} entities, {RelCount} relationships",
                chunk.ChunkId, entities.Count, relationships.Count);

            return (entities, relationships);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse entity extraction JSON for chunk {ChunkId} — raw response: {Response}",
                chunk.ChunkId, response);
            return (new List<GraphEntity>(), new List<GraphRelationship>());
        }
    }
}