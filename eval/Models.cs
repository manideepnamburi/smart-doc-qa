using System.Text.Json.Serialization;

namespace SmartDocQA.Eval;

// ── Local copies of the real API's enums (see .csproj comment on why this
// project doesn't reference SmartDocQA.Domain directly). Declaration ORDER
// must match src/SmartDocQA.Domain/Enums/DomainEnums.cs exactly, since the
// API serializes these as raw integers (System.Text.Json's default enum
// behavior) and .NET maps int -> enum purely by ordinal position, not name.
// If the real enums are ever reordered or extended, update these to match.

public enum ChunkType { Text, Table, Chart, OcrPage }
public enum ConfidenceLevel { High, Medium, Low, NotFound }
public enum RetrievalMode { DenseOnly, SparseOnly, Hybrid, HybridWithGraph }
public enum AnswerSource { Document, LLMFallback, NotFound }

// ── Golden dataset ──────────────────────────────────────────────────────────

public record GoldenDataset(
    string DocumentFileName,
    string Description,
    List<GoldenEntry> Entries);

public record GoldenEntry(
    string Id,
    string Question,
    string ExpectedAnswer,
    List<string> ExpectedKeywords,
    List<int>? ExpectedPageNumbers,
    string Category,
    string? Notes = null);

// ── API contract (mirrors QueryRequest / QAResult in the main solution) ────
// Kept as a plain local copy rather than a project reference — see the
// .csproj comment for why. If the real API contract changes, these records
// need to be updated to match; a mismatch will surface immediately as a
// deserialization failure, which is a cheap, loud failure mode by design.

public record QueryRequest(
    string Question,
    bool UseGraph = false,
    bool UseReranking = false,
    bool FallbackToLLM = false,
    string? DocumentIdFilter = null);

public record ApiCitation(
    string ChunkId,
    string FileName,
    int PageNumber,
    ChunkType ChunkType,
    string RelevantExcerpt);

public record ApiQAResult(
    string Answer,
    List<ApiCitation> Citations,
    ConfidenceLevel Confidence,
    RetrievalMode RetrievalMode,
    AnswerSource AnswerSource,
    int ChunksRetrieved,
    int ChunksAfterRerank,
    [property: JsonPropertyName("processingTime")] string? ProcessingTime);

// ── Judge configuration (read from judges-config.json) ─────────────────────

public record JudgesConfigFile(List<JudgeConfig> Judges);

public record JudgeConfig(
    string Name,
    string Provider,       // "anthropic" or "openai"
    string Model,
    string ApiKeyEnvVar,   // name of the env var holding this judge's key — never the key itself
    bool Enabled);

// ── Per-question run result (API response + one JudgeScore per active judge)

public class QuestionResult
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public required string ExpectedAnswer { get; init; }
    public required List<int>? ExpectedPageNumbers { get; init; }
    public required string Category { get; init; }

    public string? ActualAnswer { get; set; }
    public List<int> ActualCitedPages { get; set; } = new();
    public string? AnswerSource { get; set; }
    public int ChunksRetrieved { get; set; }
    public bool ApiCallSucceeded { get; set; }
    public string? ApiError { get; set; }

    // Retrieval hit-rate: did the expected page show up among the citations?
    // Null for "not_found" category questions where there is no expected page.
    public bool? RetrievalHit { get; set; }

    // Keyed by judge name (e.g. "haiku", "gpt4o-mini") so results from
    // multiple judges on the same question sit side by side for comparison.
    public Dictionary<string, JudgeScore> JudgeScores { get; set; } = new();
}

// ── LLM-as-judge output (strict rubric, parsed from Haiku's JSON reply) ─────

public class JudgeScore
{
    [JsonPropertyName("faithfulness_score")]
    public int FaithfulnessScore { get; set; }   // 1-5

    [JsonPropertyName("faithfulness_reasoning")]
    public string FaithfulnessReasoning { get; set; } = "";

    [JsonPropertyName("citation_accuracy_score")]
    public int CitationAccuracyScore { get; set; }   // 1-5

    [JsonPropertyName("citation_accuracy_reasoning")]
    public string CitationAccuracyReasoning { get; set; } = "";

    [JsonPropertyName("overall_pass")]
    public bool OverallPass { get; set; }
}

// ── Aggregate report ─────────────────────────────────────────────────────────

public class JudgeAggregate
{
    public required string JudgeName { get; init; }
    public required string Model { get; init; }
    public double AverageFaithfulness { get; set; }
    public double AverageCitationAccuracy { get; set; }
    public int OverallPassCount { get; set; }
    public int TotalScored { get; set; }
    public double OverallPassRate => TotalScored == 0 ? 0 : (double)OverallPassCount / TotalScored;
}

public class EvalReport
{
    public DateTime RunTimestamp { get; set; } = DateTime.UtcNow;
    public int TotalQuestions { get; set; }
    public int ApiCallsSucceeded { get; set; }
    public int ApiCallsFailed { get; set; }

    public int RetrievalHits { get; set; }
    public int RetrievalMisses { get; set; }
    public double RetrievalHitRate => (RetrievalHits + RetrievalMisses) == 0
        ? 0 : (double)RetrievalHits / (RetrievalHits + RetrievalMisses);

    // One aggregate per active judge, so multi-judge runs are comparable side by side.
    public List<JudgeAggregate> JudgeAggregates { get; set; } = new();

    public List<QuestionResult> Results { get; set; } = new();
}
