using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.LLM;

/// <summary>
/// Direct HTTP client for Anthropic Claude API.
/// No SDK dependency — pure HttpClient with retry via Polly (registered in DI).
/// </summary>
public class ClaudeHttpClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly ILogger<ClaudeHttpClient> _logger;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    public ClaudeHttpClient(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        ILogger<ClaudeHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default,
        string? modelOverride = null)
    {
        var payload = new
        {
            model = modelOverride ?? _options.ChatModel,
            max_tokens = _options.MaxTokens,
            temperature = _options.Temperature,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };
        return await SendAsync(payload, ct);
    }

    public async Task<string> CompleteWithVisionAsync(
        string systemPrompt,
        string userPrompt,
        byte[] imageBytes,
        CancellationToken ct = default)
    {
        var base64Image = Convert.ToBase64String(imageBytes);

        var payload = new
        {
            model = _options.VisionModel,
            max_tokens = _options.MaxTokens,
            temperature = _options.Temperature,
            system = systemPrompt,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = "image/png",
                                data = base64Image
                            }
                        },
                        new { type = "text", text = userPrompt }
                    }
                }
            }
        };

        return await SendAsync(payload, ct);
    }

    private async Task<string> SendAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug("Calling Claude API: model={Model}", _options.ChatModel);

        var response = await _httpClient.PostAsync(ApiUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);

        // Extract text from content[0].text
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();

        return text ?? string.Empty;
    }
}
