namespace SmartDocQA.Domain.Models;

/// <summary>
/// A directed relationship between two entities in the Neo4j knowledge
/// graph (see IEntityExtractor / ClaudeEntityExtractor). RelationType is
/// free-form -- REPORTS_TO, MENTIONED_WITH, CAUSED_BY, etc. -- assigned
/// by the extraction prompt rather than a fixed enum, matching
/// GraphEntity's Type field for the same reason.
/// </summary>
public record GraphRelationship(
    string FromEntity,
    string ToEntity,
    string RelationType,   // REPORTS_TO, MENTIONED_WITH, CAUSED_BY, etc.
    string DocumentId
);