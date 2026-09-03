namespace SmartDocQA.Domain.Models;

/// <summary>
/// A named entity extracted from a document chunk for the Neo4j knowledge
/// graph (see IEntityExtractor / ClaudeEntityExtractor). Type is a free-
/// form category label -- PERSON, ORG, CONCEPT, METRIC, DATE, etc. --
/// assigned by the extraction prompt, not a fixed enum, since the range
/// of entity types varies by document domain.
/// </summary>
public record GraphEntity(
    string Name,
    string Type,       // PERSON, ORG, CONCEPT, METRIC, DATE, etc.
    string DocumentId,
    int PageNumber
);
