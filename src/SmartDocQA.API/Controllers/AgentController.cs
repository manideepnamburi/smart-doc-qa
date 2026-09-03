using Microsoft.AspNetCore.Mvc;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.API.Controllers;

/// <summary>
/// Exposes the agentic query pipeline (Phase 7.5) as its own endpoint,
/// deliberately separate from DocumentsController's existing /query
/// route. See AgentQueryUseCase's XML doc comment for the full rationale
/// on why this is a distinct pipeline rather than a mode flag on the
/// existing one.
///
/// Route: POST /api/agent/query
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AgentController : ControllerBase
{
    private readonly AgentQueryUseCase _agentQueryUseCase;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        AgentQueryUseCase agentQueryUseCase,
        ILogger<AgentController> logger)
    {
        _agentQueryUseCase = agentQueryUseCase;
        _logger = logger;
    }

    // ─── POST /api/agent/query ────────────────────────────────────────────────

    /// <summary>
    /// Ask a question via the agentic pipeline: the question is decomposed
    /// into sub-questions (when compound), each sub-question is
    /// independently retrieved and verified against its evidence with
    /// automatic retry/reformulation on failure, and the final answer is
    /// synthesized with citations traceable back to which sub-question
    /// each fact came from.
    ///
    /// Reuses the same QueryRequest DTO as /api/documents/query -- the
    /// request shape is identical; only the pipeline underneath differs.
    /// </summary>
    [HttpPost("query")]
    public async Task<IActionResult> Query(
        [FromBody] QueryRequest request, CancellationToken ct)
    {
        var query = new QAQuery(
            Question: request.Question,
            UseGraph: request.UseGraph,
            UseReranking: request.UseReranking,
            FallbackToLLM: request.FallbackToLLM,
            DocumentIdFilter: request.DocumentIdFilter,
            History: request.History);

        var result = await _agentQueryUseCase.ExecuteAsync(query, ct);
        return Ok(result);
    }
}
