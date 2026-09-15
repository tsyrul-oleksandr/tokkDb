namespace TokkDb.Assistant.Agents;

/// <summary>
/// How this assembly compares the names the storage gave it: the contract's own rule (SC-4),
/// ordinal, said once here because the contract keeps its helper internal.
/// </summary>
internal static class Names
{
    public static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    public static StringComparer Comparer => StringComparer.Ordinal;
}
