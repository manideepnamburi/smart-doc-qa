using System.Net.Http.Json;
using System.Text.Json;

namespace SmartDocQA.Eval;

/// <summary>
/// Common interface so Program.cs can call any configured judge the same way,
/// regardless of which provider it's backed by.
/// </summary>
public interface IJudgeCaller
{
    Task<JudgeScore> JudgeAsync(string rubricTemplate, GoldenEntry entry, QuestionResult result);
}

internal static class RubricFiller
{
    public static string Fill(string rubricTemplate, GoldenEntry entry, QuestionResult result) =>
        rubricTemplate
            .Replace("{{question}}", entry.Question)
            .Replace("{{expected_answer}}", entry.ExpectedAnswer)
            .Replace("{{actual_answer}}", result.ActualAnswer ?? "(no answer returned)")
            .Replace("{{cited_pages}}", string.Join(", ", result.ActualCitedPages))
            .Replace("{{expected_page}}", entry.ExpectedPageNumbers is { Count: > 0 }
                ? string.Join(" or ", entry.ExpectedPageNumbers)
                : "N/A (not-found question)");

    public static JudgeScore ParseJudgeJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            text = text[(text.IndexOf('\n') + 1)..];
            var lastFence = text.LastIndexOf("```");
            if (lastFence >= 0) text = text[..lastFence];
        }

        return JsonSerializer.Deserialize<JudgeScore>(text)
            ?? throw new InvalidOperationException($"Could not parse judge response as JSON: {text}");
    }
}

/// <summary>
/// Judge caller for any Claude model via Anthropic's /v1/messages API.
/// </summary>
public class AnthropicJudgeCaller : IJudgeCaller
{
    private readonly string _model;
    private readonly HttpClient _client;

    public AnthropicJudgeCaller(string model, string apiKey)
    {
        _model = model;
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<JudgeScore> JudgeAsync(string rubricTemplate, GoldenEntry entry, QuestionResult result)
    {
        var prompt = RubricFiller.Fill(rubricTemplate, entry, result);

        var body = new
        {
            model = _model,
            max_tokens = 500,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await _client.PostAsJsonAsync("/v1/messages", body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = json.GetProperty("content")[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Anthropic judge response had no text content.");

        return RubricFiller.ParseJudgeJson(text);
    }
}

/// <summary>
/// Judge caller for any OpenAI chat model via the /v1/chat/completions API.
/// Included so you can compare a Claude judge against a non-Claude judge —
/// useful for sanity-checking that the judge itself isn't systematically
/// biased toward Claude-style phrasing.
/// </summary>
public class OpenAiJudgeCaller : IJudgeCaller
{
    private readonly string _model;
    private readonly HttpClient _client;

    public OpenAiJudgeCaller(string model, string apiKey)
    {
        _model = model;
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<JudgeScore> JudgeAsync(string rubricTemplate, GoldenEntry entry, QuestionResult result)
    {
        var prompt = RubricFiller.Fill(rubricTemplate, entry, result);

        var body = new
        {
            model = _model,
            max_tokens = 500,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("OpenAI judge response had no message content.");

        return RubricFiller.ParseJudgeJson(text);
    }
}
