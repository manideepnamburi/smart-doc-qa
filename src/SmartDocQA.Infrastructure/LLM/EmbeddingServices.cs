using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.LLM;

// ─── Azure OpenAI ─────────────────────────────────────────────────────────────

public class AzureOpenAIEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger _logger;

    public AzureOpenAIEmbeddingService(EmbeddingOptions options, ILogger logger)
    {
        _logger = logger;

        // Azure OpenAI client → embedding generator
        var azureClient = new AzureOpenAIClient(
            new Uri(options.AzureEndpoint),
            new AzureKeyCredential(options.AzureApiKey));

        _generator = azureClient
            .GetEmbeddingClient(options.AzureEmbeddingDeployment)
            .AsIEmbeddingGenerator();
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        _logger.LogDebug("[AzureOpenAI] Embedding {Length} chars", text.Length);
        var vector = await _generator.GenerateVectorAsync(text, cancellationToken: ct);
        return vector.ToArray();
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default)
    {
        _logger.LogDebug("[AzureOpenAI] Batch embedding {Count} texts", texts.Count);
        var embeddings = await _generator.GenerateAsync(texts, cancellationToken: ct);
        return embeddings.Select(e => e.Vector.ToArray()).ToList();
    }
}

// ─── OpenAI Direct ────────────────────────────────────────────────────────────

// ─── OpenAI Direct (HTTP) ─────────────────────────────────────────────────────

public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger _logger;

    public OpenAIEmbeddingService(EmbeddingOptions options, ILogger logger)
    {
        _logger = logger;
        _model = options.OpenAIEmbeddingModel;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add(
            "Authorization", $"Bearer {options.OpenAIApiKey}");
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        _logger.LogDebug("[OpenAI] Embedding {Length} chars", text.Length);
        return await EmbedSingleAsync(text, ct);
    }

    public async Task<List<float[]>> EmbedBatchAsync(
        List<string> texts, CancellationToken ct = default)
    {
        _logger.LogDebug("[OpenAI] Batch embedding {Count} texts", texts.Count);
        var results = new List<float[]>();
        foreach (var text in texts)
            results.Add(await EmbedSingleAsync(text, ct));
        return results;
    }

    private async Task<float[]> EmbedSingleAsync(string text, CancellationToken ct)
    {
        var payload = new { model = _model, input = text };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new StringContent(
            json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            "https://api.openai.com/v1/embeddings", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        return doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(v => v.GetSingle())
            .ToArray();
    }
}

// ─── Ollama Local ─────────────────────────────────────────────────────────────

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger _logger;

    public OllamaEmbeddingService(EmbeddingOptions options, ILogger logger)
    {
        _logger = logger;

        // OllamaSharp implements IEmbeddingGenerator natively
        _generator = new OllamaApiClient(
            new Uri(options.OllamaBaseUrl),
            options.OllamaEmbeddingModel);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        _logger.LogDebug("[Ollama] Embedding {Length} chars", text.Length);
        var vector = await _generator.GenerateVectorAsync(text, cancellationToken: ct);
        return vector.ToArray();
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default)
    {
        _logger.LogDebug("[Ollama] Batch embedding {Count} texts", texts.Count);
        var embeddings = await _generator.GenerateAsync(texts, cancellationToken: ct);
        return embeddings.Select(e => e.Vector.ToArray()).ToList();
    }
}
