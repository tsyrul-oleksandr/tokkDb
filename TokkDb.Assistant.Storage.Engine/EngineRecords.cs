using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// A value found in a record that is not of its column's type, and what can be made of it.
///
/// SC-6b is the reason this exists. A column that was retyped leaves values behind, and the
/// engine's own answer - <c>ValueMigration</c> turns what it cannot read into null - is the one
/// answer the contract forbids. So the serializer wraps every mismatched value in one of these
/// and the storage decides: converted where it converts, kept and flagged where it does not.
///
/// <b>Both kinds are carried, not only the broken ones.</b> A value that converts on read is
/// still stored in the old type, so it is still sitting in the index under a key of that type -
/// which means a range walk for the new type never reaches it. That is SC-6c's "records excluded
/// as not of the column's type", and counting only the unconvertible ones would under-report it
/// by exactly the records a converge would have fixed.
/// </summary>
internal sealed record StoredAs(object? Value, ColumnType Recorded, object? Converted, bool CanConvert)
{
    /// <summary>What a read hands back: the converted value where there is one, the original where not.</summary>
    public object? AsRead => CanConvert ? Converted : Value;
}

/// <summary>
/// Turning what the serializer produced into a <see cref="StorageRecord"/>, and back.
/// </summary>
internal static class EngineRecords
{
    /// <summary>
    /// One record, with its unreadable values kept and named (SC-6b).
    /// </summary>
    public static StorageRecord ToRecord(string collectionName, Ulid id, Dictionary<string, object?> fields)
    {
        List<string>? attention = null;
        Dictionary<string, object?>? unwrapped = null;

        foreach (var (column, value) in fields)
        {
            if (value is not StoredAs stored) continue;

            unwrapped ??= new Dictionary<string, object?>(fields, StorageNames.Comparer);
            unwrapped[column] = stored.AsRead;

            if (!stored.CanConvert)
            {
                attention ??= [];
                attention.Add(column);
            }
        }

        return new StorageRecord(id, collectionName, unwrapped ?? fields, attention);
    }

    /// <summary>
    /// The columns of one record whose stored value is not of the column's type - both the ones
    /// a read converts and the ones it cannot. What SC-6c counts.
    /// </summary>
    public static IEnumerable<(string Column, StoredAs Value)> NotOfColumnType(
        Dictionary<string, object?> fields)
    {
        foreach (var (column, value) in fields)
        {
            if (value is StoredAs stored) yield return (column, stored);
        }
    }
}
