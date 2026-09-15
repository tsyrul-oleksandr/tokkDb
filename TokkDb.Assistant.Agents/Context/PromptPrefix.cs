using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Context;

/// <summary>
/// The prefix of an operation's prompt, assembled in one order - instructions, schema block,
/// tools block - so that it is byte-identical between calls of that operation while the storage
/// is unchanged (AG-6a).
///
/// That is what the model's prefix cache is keyed on, and it buys latency: the cached prefix
/// occupies exactly as much of the window as an uncached one, so it buys no headroom (§6.1). A
/// reordering of two blocks is invisible in review and costs the whole cache, which is why the
/// order lives in one method and a test hashes the result rather than trusting it.
/// </summary>
public static class PromptPrefix
{
    public const string Separator = "\n\n";

    /// <summary>Instructions, then the schema block, then the tools block. Nothing per call.</summary>
    public static string Assemble(OperationDeclaration operation, string schemaBlock, ToolCatalog tools)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(schemaBlock);
        ArgumentNullException.ThrowIfNull(tools);

        return operation.Instructions.Trim()
               + Separator
               + schemaBlock.Trim()
               + Separator
               + tools.Describe(operation).Trim();
    }

    public static string Hash(string prefix) => Hashes.Of(prefix);
}
