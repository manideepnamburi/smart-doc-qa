using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Prompts;

/// <summary>
/// Loads prompt templates from the /Prompts folder.
/// Variables in templates use {{variable_name}} syntax.
/// Prompts are cached in memory after first load.
/// </summary>
public class FilePromptLoader : IPromptLoader
{
    private readonly PromptsOptions _options;
    private readonly Dictionary<string, string> _cache = new();
    private readonly object _lock = new();   // object for net8 compatibility

    public FilePromptLoader(IOptions<PromptsOptions> options)
        => _options = options.Value;

    public string Load(string promptFileName)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(promptFileName, out var cached))
                return cached;

            // Try 1: relative to app base directory (works in production + dotnet run)
            var appBasePath = Path.Combine(
                AppContext.BaseDirectory, _options.BasePath, promptFileName);

            // Try 2: relative to current working directory (works in VS debugger)
            var cwdPath = Path.Combine(
                Directory.GetCurrentDirectory(), _options.BasePath, promptFileName);

            var path = File.Exists(appBasePath) ? appBasePath
                     : File.Exists(cwdPath) ? cwdPath
                     : null;

            if (path is null)
                throw new FileNotFoundException(
                    $"Prompt file '{promptFileName}' not found. Searched:\n" +
                    $"  1) {appBasePath}\n" +
                    $"  2) {cwdPath}\n" +
                    $"Check PromptsOptions.BasePath in appsettings.json " +
                    $"and ensure Prompts are set to CopyToOutputDirectory=PreserveNewest.");

            var content = File.ReadAllText(path);
            _cache[promptFileName] = content;
            return content;
        }
    }

    public string LoadAndFill(string promptFileName, Dictionary<string, string> variables)
    {
        var template = Load(promptFileName);
        foreach (var (key, value) in variables)
            template = template.Replace($"{{{{{key}}}}}", value);   // {{key}} syntax
        return template;
    }
}
