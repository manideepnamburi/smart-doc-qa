using System.Net.Http.Json;
using System.Text.Json;

namespace SmartDocQA.Eval;

public static class Program
{
    private static readonly string ApiBaseUrl =
        Environment.GetEnvironmentVariable("SMARTDOCQA_API_BASE_URL")
        ?? "https://localhost:7001";

    public static async Task<int> Main(string[] args)
    {
        var datasetPath = args.Length > 0 ? args[0] : "golden-dataset.json";
        var reportPath = args.Length > 1 ? args[1] : "eval-report.json";
        var judgesConfigPath = args.Length > 2 ? args[2] : "judges-config.json";

        if (!File.Exists(datasetPath))
        {
            Console.Error.WriteLine($"Golden dataset not found at '{datasetPath}'.");
            return 1;
        }
        if (!File.Exists(judgesConfigPath))
        {
            Console.Error.WriteLine($"Judges config not found at '{judgesConfigPath}'.");
            return 1;
        }

        var judgesConfig = JsonSerializer.Deserialize<JudgesConfigFile>(
            await File.ReadAllTextAsync(judgesConfigPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidOperationException("Failed to parse judges-config.json.");

        var activeJudges = judgesConfig.Judges.Where(j => j.Enabled).ToList();
        if (activeJudges.Count == 0)
        {
            Console.Error.WriteLine(
                "No judges are enabled in judges-config.json. Set \"enabled\": true on at least one.");
            return 1;
        }

        var judgeCallers = new Dictionary<string, IJudgeCaller>();
        foreach (var judge in activeJudges)
        {
            var apiKey = Environment.GetEnvironmentVariable(judge.ApiKeyEnvVar);
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.Error.WriteLine(
                    $"Judge '{judge.Name}' is enabled but its API key env var " +
                    $"'{judge.ApiKeyEnvVar}' is not set. Skipping this judge.");
                continue;
            }

            judgeCallers[judge.Name] = judge.Provider.ToLower() switch
            {
                "anthropic" => new AnthropicJudgeCaller(judge.Model, apiKey),
                "openai" => new OpenAiJudgeCaller(judge.Model, apiKey),
                _ => throw new InvalidOperationException(
                    $"Unknown judge provider '{judge.Provider}' for judge '{judge.Name}'. " +
                    $"Valid values: anthropic, openai")
            };
        }

        if (judgeCallers.Count == 0)
        {
            Console.Error.WriteLine("No judges could be initialized (all missing API keys). Aborting.");
            return 1;
        }

        var rubricTemplate = await LoadJudgeRubric();
        var dataset = JsonSerializer.Deserialize<GoldenDataset>(
            await File.ReadAllTextAsync(datasetPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidOperationException("Failed to parse golden dataset.");

        Console.WriteLine($"Loaded {dataset.Entries.Count} golden questions for '{dataset.DocumentFileName}'.");
        Console.WriteLine($"Target API: {ApiBaseUrl}");
        Console.WriteLine($"Active judges: {string.Join(", ", judgeCallers.Keys)}");
        Console.WriteLine();

        using var apiClient = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };

        var report = new EvalReport { TotalQuestions = dataset.Entries.Count };

        foreach (var entry in dataset.Entries)
        {
            Console.WriteLine($"[{entry.Id}] {entry.Question}");

            var result = new QuestionResult
            {
                Id = entry.Id,
                Question = entry.Question,
                ExpectedAnswer = entry.ExpectedAnswer,
                ExpectedPageNumber = entry.ExpectedPageNumber,
                Category = entry.Category
            };

            try
            {
                var request = new QueryRequest(
                    Question: entry.Question,
                    UseGraph: false,
                    UseReranking: true,
                    FallbackToLLM: false);

                var response = await apiClient.PostAsJsonAsync("/api/documents/query", request);
                response.EnsureSuccessStatusCode();

                var qaResult = await response.Content.ReadFromJsonAsync<ApiQAResult>();
                if (qaResult is null)
                    throw new InvalidOperationException("API returned an empty/unparseable result.");

                result.ApiCallSucceeded = true;
                result.ActualAnswer = qaResult.Answer;
                result.ActualCitedPages = qaResult.Citations.Select(c => c.PageNumber).Distinct().ToList();
                result.AnswerSource = qaResult.AnswerSource;
                result.ChunksRetrieved = qaResult.ChunksRetrieved;

                report.ApiCallsSucceeded++;
            }
            catch (Exception ex)
            {
                result.ApiCallSucceeded = false;
                result.ApiError = ex.Message;
                report.ApiCallsFailed++;
                Console.WriteLine($"  API call failed: {ex.Message}");
                report.Results.Add(result);
                continue;
            }

            if (entry.Category == "not_found")
                result.RetrievalHit = result.AnswerSource == "NotFound";
            else if (entry.ExpectedPageNumber.HasValue)
                result.RetrievalHit = result.ActualCitedPages
                    .Any(p => Math.Abs(p - entry.ExpectedPageNumber.Value) <= 1);

            if (result.RetrievalHit == true) report.RetrievalHits++;
            else if (result.RetrievalHit == false) report.RetrievalMisses++;

            Console.WriteLine($"  Retrieval hit: {result.RetrievalHit} | Cited pages: [{string.Join(",", result.ActualCitedPages)}]");

            foreach (var (judgeName, caller) in judgeCallers)
            {
                try
                {
                    var score = await caller.JudgeAsync(rubricTemplate, entry, result);
                    result.JudgeScores[judgeName] = score;
                    Console.WriteLine($"  [{judgeName}] faithfulness={score.FaithfulnessScore}, citation={score.CitationAccuracyScore}, pass={score.OverallPass}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{judgeName}] judge call failed: {ex.Message}");
                }
            }

            report.Results.Add(result);
            Console.WriteLine();
        }

        foreach (var judgeName in judgeCallers.Keys)
        {
            var scored = report.Results
                .Where(r => r.JudgeScores.ContainsKey(judgeName))
                .Select(r => r.JudgeScores[judgeName])
                .ToList();

            if (scored.Count == 0) continue;

            report.JudgeAggregates.Add(new JudgeAggregate
            {
                JudgeName = judgeName,
                Model = activeJudges.First(j => j.Name == judgeName).Model,
                AverageFaithfulness = scored.Average(s => s.FaithfulnessScore),
                AverageCitationAccuracy = scored.Average(s => s.CitationAccuracyScore),
                OverallPassCount = scored.Count(s => s.OverallPass),
                TotalScored = scored.Count
            });
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));

        PrintSummary(report);
        Console.WriteLine($"\nFull report written to: {reportPath}");

        const double passThreshold = 0.75;
        var gateJudge = report.JudgeAggregates.FirstOrDefault();
        return (gateJudge?.OverallPassRate ?? 0) >= passThreshold ? 0 : 1;
    }

    private static async Task<string> LoadJudgeRubric()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "judge_rubric.txt");
        if (!File.Exists(path))
            path = Path.Combine("Prompts", "judge_rubric.txt");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Judge rubric prompt not found. Ensure Prompts/judge_rubric.txt is set to CopyToOutputDirectory=PreserveNewest.");

        return await File.ReadAllTextAsync(path);
    }

    private static void PrintSummary(EvalReport report)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("EVAL SUMMARY");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Total questions:         {report.TotalQuestions}");
        Console.WriteLine($"API calls succeeded:     {report.ApiCallsSucceeded}");
        Console.WriteLine($"API calls failed:        {report.ApiCallsFailed}");
        Console.WriteLine($"Retrieval hit-rate:      {report.RetrievalHitRate:P1} ({report.RetrievalHits}/{report.RetrievalHits + report.RetrievalMisses})");
        Console.WriteLine();

        foreach (var agg in report.JudgeAggregates)
        {
            Console.WriteLine($"Judge: {agg.JudgeName} ({agg.Model})");
            Console.WriteLine($"  Avg faithfulness (1-5):  {agg.AverageFaithfulness:F2}");
            Console.WriteLine($"  Avg citation acc (1-5):  {agg.AverageCitationAccuracy:F2}");
            Console.WriteLine($"  Overall pass rate:       {agg.OverallPassRate:P1} ({agg.OverallPassCount}/{agg.TotalScored})");
            Console.WriteLine();
        }

        if (report.JudgeAggregates.Count > 1)
        {
            Console.WriteLine("Cross-judge comparison (questions where judges disagreed on pass/fail):");
            foreach (var r in report.Results.Where(r => r.JudgeScores.Count > 1))
            {
                var passVotes = r.JudgeScores.Values.Select(s => s.OverallPass).Distinct().Count();
                if (passVotes > 1)
                {
                    Console.WriteLine($"  [{r.Id}] {r.Question}");
                    foreach (var (name, score) in r.JudgeScores)
                        Console.WriteLine($"    {name}: pass={score.OverallPass} (faith={score.FaithfulnessScore}, cite={score.CitationAccuracyScore})");
                }
            }
        }
    }
}
