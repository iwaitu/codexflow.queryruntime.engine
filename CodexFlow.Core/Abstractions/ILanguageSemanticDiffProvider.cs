using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// Provides language-specific semantic difference analysis.
/// </summary>
public interface ILanguageSemanticDiffProvider
{
    /// <summary>
    /// Checks if this provider can handle the given file extension.
    /// </summary>
    /// <param name="extension">File extension (e.g. ".cs", ".py")</param>
    bool CanHandle(string extension);

    /// <summary>
    /// Analyzes the semantic difference between two files.
    /// </summary>
    /// <param name="mainPath">Path to the original file.</param>
    /// <param name="shadowPath">Path to the modified file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SemanticDiffResult> AnalyzeAsync(string mainPath, string shadowPath, CancellationToken ct);
}
