namespace TokkDb.LLM.Storage;

/// <summary>
/// Where a <see cref="SemanticTypeRegistry"/> keeps its definitions.
///
/// The registry owns what a definition must satisfy — the name shape, the parent hierarchy,
/// which rules a base type can carry — and that is the same wherever the definitions are
/// kept. Only the keeping differs, so only the keeping is behind this.
///
/// Definitions arrive here already normalised and validated. A store persists what it is
/// given and hands it back; it does not check it again, and it must not change it.
/// </summary>
public interface ISemanticTypeStore
{
    IReadOnlyCollection<SemanticTypeDefinition> Load();

    void Save(SemanticTypeDefinition definition);

    bool Delete(string name);
}

/// <summary>
/// The default: definitions live as long as the registry does. What the registry did before
/// anything persisted them, kept as the store for tests and for a run that has no database.
/// </summary>
public sealed class InMemorySemanticTypeStore : ISemanticTypeStore
{
    private readonly List<SemanticTypeDefinition> _definitions = [];

    public IReadOnlyCollection<SemanticTypeDefinition> Load() => _definitions.ToArray();

    public void Save(SemanticTypeDefinition definition)
    {
        _definitions.RemoveAll(existing =>
            string.Equals(existing.Name, definition.Name, StringComparison.Ordinal));
        _definitions.Add(definition);
    }

    public bool Delete(string name)
    {
        return _definitions.RemoveAll(existing =>
            string.Equals(existing.Name, name, StringComparison.Ordinal)) > 0;
    }
}
