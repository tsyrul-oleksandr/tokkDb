using System.ComponentModel;
using Microsoft.Extensions.AI;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Operations;

/// <summary>
/// Every tool there is, in C#, and the scoping that hands an operation only its own (D-5, AG-1).
///
/// The model is never handed the whole surface. An operation names the tools it may have, the
/// runner asks this for exactly those, and a name the catalogue does not know is refused at
/// registration rather than discovered at the first call. Today every shipped operation names
/// none - the digest is inlined and the answer is structured, because a read tool was measured
/// as a loop and a cost (D-5) - so what is here is the two reads the digest may leave out and the
/// mechanism a future acting tool will use.
/// </summary>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

    /// <summary>The catalogue with its built-in tools over one storage.</summary>
    public ToolCatalog(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        Add(AIFunctionFactory.Create(
            ([Description("The name of a thing stored, exactly as listed")] string name) =>
                storage.GetCollectionDefinition(name) is { } definition
                    ? SchemaDigest.Line(definition)
                    : $"Nothing stored is called '{name}'.",
            "describe_thing",
            "Describes one thing the storage holds that the digest only named: its purpose and its fields."));

        Add(AIFunctionFactory.Create(
            ([Description("The name of a thing stored, exactly as listed")] string name) =>
                storage.GetCollectionDefinition(name) is null
                    ? -1
                    : Convert.ToInt32(storage.ExecuteQuery(
                        new StorageQuery(name).Computing(new QueryAggregate(AggregateFunction.Count)).Taking(0)).Aggregates["count"]),
            "count_records",
            "How many records one thing holds."));
    }

    /// <summary>An empty catalogue, for tests that bring their own tools.</summary>
    public ToolCatalog()
    {
    }

    public IReadOnlyCollection<string> Names => _tools.Keys;

    /// <summary>Adds a tool. A second tool under one name replaces the first.</summary>
    public ToolCatalog Add(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
        return this;
    }

    /// <summary>The tools this operation declared, and no others (D-5).</summary>
    /// <exception cref="UnknownToolException">The operation names a tool the catalogue does not have.</exception>
    public IReadOnlyList<AITool> For(OperationDeclaration operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var scoped = new List<AITool>(operation.Tools.Count);

        foreach (var name in operation.Tools)
        {
            if (!_tools.TryGetValue(name, out var tool))
            {
                throw new UnknownToolException(operation.Name, name);
            }

            scoped.Add(tool);
        }

        return scoped;
    }

    /// <summary>The tools block of the prefix (AG-6a): one line per tool, in the operation's order, or none.</summary>
    public string Describe(OperationDeclaration operation) =>
        operation.Tools.Count == 0
            ? "tools: none"
            : "tools:\n" + string.Join("\n", For(operation).Select(static tool => $"- {tool.Name}: {tool.Description}"));
}

/// <summary>An operation named a tool that is not in the catalogue.</summary>
public sealed class UnknownToolException : Exception
{
    public UnknownToolException(string operation, string tool)
        : base($"Operation '{operation}' names a tool called '{tool}', and the catalogue has no such tool.")
    {
        Operation = operation;
        Tool = tool;
    }

    public string Operation { get; }
    public string Tool { get; }
}
