using TokkDb.Pages;
using TokkDb.Pages.Records;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// D-4: the semantic type registry, kept in <c>_semanticTypes</c>.
///
/// There is no storage mechanism here — that was the point of choosing Option B. A semantic
/// type is a document, written through the same pages, journal and transaction as a user
/// record, and everything this class does beyond mapping is bookkeeping of which document
/// belongs to which name.
///
/// Deliberately separate from the collection descriptors: a semantic type is metadata the
/// model rewrites as it learns about the data, and a rewrite of it must not touch the
/// structural schema, whose version records what the stored records mean.
/// </summary>
public sealed class TokkDbSemanticTypeStore : ISemanticTypeStore
{
    private readonly EngineConnection _connection;

    // Name to the identity its document is stored under, so a re-registration replaces the
    // document rather than adding a second one for the same name.
    private readonly Dictionary<string, Ulid> _identities = new(StringComparer.Ordinal);

    public TokkDbSemanticTypeStore(EngineConnection connection)
    {
        _connection = connection;
        // Describes its own documents, the way the engine's own system collections do, so the
        // shape of a semantic type is readable from the catalogue and not only from code.
        _connection.DescribeSystemCollection(SystemCollections.SemanticTypes,
            SemanticTypeDocument.CreateColumns());
    }

    public IReadOnlyCollection<SemanticTypeDefinition> Load()
    {
        var definitions = new List<SemanticTypeDefinition>();
        _identities.Clear();
        foreach (var (id, document) in _connection.SystemDocuments.ReadAll(SystemCollections.SemanticTypes))
        {
            var definition = SemanticTypeDocument.Read(document);
            _identities[definition.Name] = id;
            definitions.Add(definition);
        }

        // Parents before children: the registry validates a hierarchy against what it has
        // already seen, and the order documents come off the pages is the order they were
        // written in, which a re-registration changes.
        return Ordered(definitions);
    }

    public void Save(SemanticTypeDefinition definition)
    {
        if (!_identities.TryGetValue(definition.Name, out var id))
        {
            id = RecordIdentity.Next();
            _identities[definition.Name] = id;
        }

        try
        {
            _connection.InTransaction(() => _connection.SystemDocuments.Write(
                SystemCollections.SemanticTypes, id, SemanticTypeDocument.Write(id, definition)));
        }
        catch
        {
            // A name whose first write failed has no document, so remembering its identity
            // would leave the store claiming one exists.
            _identities.Remove(definition.Name);
            throw;
        }
    }

    public bool Delete(string name)
    {
        if (!_identities.TryGetValue(name, out var id))
        {
            return false;
        }

        var deleted = false;
        _connection.InTransaction(() =>
            deleted = _connection.SystemDocuments.Delete(SystemCollections.SemanticTypes, id));
        if (deleted)
        {
            _identities.Remove(name);
        }

        return deleted;
    }

    /// <summary>
    /// A definition is listed after the one it derives from. Storage has no order of its own
    /// worth relying on — a document moves when it is rewritten — so the hierarchy is what
    /// orders them, and a parent that is missing does not hold its children back: the registry
    /// is what refuses that, and it should be given the chance to.
    /// </summary>
    private static List<SemanticTypeDefinition> Ordered(List<SemanticTypeDefinition> definitions)
    {
        var byName = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        var ordered = new List<SemanticTypeDefinition>(definitions.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        void Place(SemanticTypeDefinition definition, HashSet<string> visiting)
        {
            if (!placed.Add(definition.Name))
            {
                return;
            }

            if (definition.ParentType is { } parentName
                && byName.TryGetValue(parentName, out var parent)
                && visiting.Add(parentName))
            {
                Place(parent, visiting);
            }

            ordered.Add(definition);
        }

        foreach (var definition in definitions)
        {
            Place(definition, [definition.Name]);
        }

        return ordered;
    }
}
