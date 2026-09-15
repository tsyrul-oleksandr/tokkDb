using System.Text;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Changes;

/// <summary>
/// The version of the storage's shape a proposal was computed against (AG-9), as a hash of every
/// definition and relation: the write re-checks it, and a mismatch re-plans rather than applying.
///
/// A hash rather than the engine's per-collection schema version, because a proposal can depend
/// on several things at once - the target and the things it was chosen over - and because the
/// contract deliberately carries nothing physical (SC-2). It moves when a definition moves and
/// never when a value does, which is what makes it the wrong check for undo (AG-11a) and the
/// right one here.
/// </summary>
public static class SchemaVersion
{
    public static string Of(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var text = new StringBuilder();

        foreach (var definition in storage.GetCollectionDefinitions().OrderBy(static definition => definition.Name, StringComparer.Ordinal))
        {
            text.Append(definition.Name).Append('|').Append(definition.Purpose).Append('|').Append(definition.DisplayRule?.Template).Append('\n');

            foreach (var column in definition.Columns)
            {
                text.Append("  ").Append(column.Name).Append(':').Append(column.Type)
                    .Append(column.Required ? ":required" : "").Append(column.Unique ? ":unique" : "").Append(column.ReadOnly ? ":readonly" : "")
                    .Append('\n');
            }
        }

        foreach (var relation in storage.GetRelations().OrderBy(static relation => relation.Name, StringComparer.Ordinal))
        {
            text.Append(relation.Name).Append(':').Append(relation).Append('\n');
        }

        return Hashes.Of(text.ToString());
    }
}
