namespace SmartDocQA.Domain.Models;

public record ParsedDocument(string FileName, List<ParsedPage> Pages, int TotalPages);
