namespace SmartDocQA.Domain.Models;

public record ParsedPage(int PageNumber, string RawText, bool IsScanned);