using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Registry;

/// <summary>
/// Recursively scans a folder and returns all supported document files.
/// Supported extensions are configured in appsettings.json → FolderScan section.
/// </summary>
public class FolderScanner : IFolderScanner
{
    private readonly FolderScanOptions _options;
    private readonly ILogger<FolderScanner> _logger;

    public FolderScanner(
        IOptions<FolderScanOptions> options,
        ILogger<FolderScanner> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public List<string> Scan(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(
                $"Folder not found: {folderPath}");

        _logger.LogInformation(
            "Scanning folder: {Folder} | Extensions: {Ext}",
            folderPath,
            string.Join(", ", _options.SupportedExtensions));

        var files = Directory
            .EnumerateFiles(
                folderPath,
                "*.*",
                SearchOption.AllDirectories)   // ← recursive — includes all subfolders
            .Where(f => _options.SupportedExtensions
                .Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        _logger.LogInformation(
            "Found {Count} supported files in {Folder}",
            files.Count, folderPath);

        // Log breakdown by extension
        var byExt = files
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .Select(g => $"{g.Key}: {g.Count()}");

        _logger.LogInformation(
            "File breakdown: {Breakdown}",
            string.Join(", ", byExt));

        return files;
    }
}
