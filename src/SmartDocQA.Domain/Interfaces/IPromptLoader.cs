namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Loads prompt templates from the Prompts/ folder.
/// </summary>
public interface IPromptLoader
{
    string Load(string promptFileName);
    string LoadAndFill(string promptFileName, Dictionary<string, string> variables);
}