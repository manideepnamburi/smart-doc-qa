namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Scans a folder recursively and returns all supported document file paths.
/// Implementation lives in Infrastructure — Application only knows the interface.
/// </summary>
public interface IFolderScanner
{
    /// <summary>
    /// Recursively scans the given folder and returns full paths
    /// of all files with supported extensions.
    /// </summary>
    List<string> Scan(string folderPath);
}
