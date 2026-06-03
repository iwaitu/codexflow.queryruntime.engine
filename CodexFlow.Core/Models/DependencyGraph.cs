using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CodexFlow.Core.Models;

/// <summary>
/// Represents a semantic node in the project's dependency graph.
/// </summary>
public class CodeNode
{
    /// <summary>
    /// Relative path to the file (e.g., "CodexFlow.Core/Models/User.cs")
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// The language of the file (C#, Python, Java, TypeScript, etc.)
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The class or interface name defined in this file.
    /// </summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// List of namespace imports (using XYZ;)
    /// </summary>
    public Collection<string> ImportedNamespaces { get; } = new();

    /// <summary>
    /// List of base classes or interfaces this type inherits from.
    /// </summary>
    public Collection<string> InheritedBaseClasses { get; } = new();

    /// <summary>
    /// List of file paths that this file depends on (outgoing edges).
    /// </summary>
    [JsonIgnore] // Avoid circular references in simple serialization
    public HashSet<string> References { get; } = new();

    /// <summary>
    /// List of file paths that depend on this file (incoming edges).
    /// </summary>
    public HashSet<string> ReferencedBy { get; } = new();

    /// <summary>
    /// The computed "criticality score" (how many files break if this one changes).
    /// </summary>
    public int CriticalityScore => ReferencedBy.Count;
}

public class DependencyGraph
{
    public Dictionary<string, CodeNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddNode(CodeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!Nodes.ContainsKey(node.FilePath))
        {
            Nodes[node.FilePath] = node;
        }
    }

    public CodeNode? GetNode(string filePath)
    {
        return Nodes.TryGetValue(filePath, out var node) ? node : null;
    }
}
