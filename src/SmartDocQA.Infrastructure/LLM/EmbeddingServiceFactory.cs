using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.LLM;

/// <summary>
/// Factory that creates the correct IEmbeddingService based on config.
/// Adding a new provider = add a new case here only. Zero other changes.
/// </summary>
public static class EmbeddingServiceFactory
{
    public static IEmbeddingService Create(
        EmbeddingOptions options,
        ILogger logger)
    {
        logger.LogInformation(
            "Embedding provider: {Provider} | Model: {Model} | VectorSize: {Size}",
            options.Provider,
            GetModelName(options),
            options.VectorSize);

        return options.Provider.ToLower() switch
        {
            "azureopenai" => new AzureOpenAIEmbeddingService(options, logger),
            "openai" => new OpenAIEmbeddingService(options, logger),
            "ollama" => new OllamaEmbeddingService(options, logger),
            _ => throw new InvalidOperationException(
                $"Unknown embedding provider: '{options.Provider}'. " +
                $"Valid values: AzureOpenAI, OpenAI, Ollama")
        };
    }

    private static string GetModelName(EmbeddingOptions o) =>
        o.Provider.ToLower() switch
        {
            "azureopenai" => o.AzureEmbeddingDeployment,
            "openai" => o.OpenAIEmbeddingModel,
            "ollama" => o.OllamaEmbeddingModel,
            _ => "unknown"
        };
}
