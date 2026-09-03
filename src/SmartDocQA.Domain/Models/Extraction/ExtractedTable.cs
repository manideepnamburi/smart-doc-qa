namespace SmartDocQA.Domain.Models;

/// <summary>
/// A table extracted from a document page via Azure Document Intelligence
/// (see ITableExtractor). Stored as Markdown so it can be embedded and
/// retrieved as a normal chunk of text, with an optional Caption pulled
/// from nearby page content to give the table context when shown to a
/// user or fed to the LLM.
/// </summary>
public record ExtractedTable(int PageNumber, string MarkdownContent, string Caption);
