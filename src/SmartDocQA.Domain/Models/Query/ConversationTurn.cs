namespace SmartDocQA.Domain.Models;

/// <summary>
/// One past exchange in an ongoing conversation. The client (React UI)
/// holds the full conversation and resends recent turns with each new
/// question -- the backend itself stays stateless, same as Anthropic's
/// own Messages API.
/// </summary>
public record ConversationTurn(string Question, string Answer);