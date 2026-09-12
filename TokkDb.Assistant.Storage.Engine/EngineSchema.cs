using TokkDb.Assistant.Storage;
using TokkDb.Pages;
using EngineColumn = TokkDb.Pages.ColumnDescriptor;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// A <see cref="CollectionDefinition"/> as the engine's catalogue holds it, and back.
///
/// D-2: the catalogue is where a schema belongs, so the definition is stored in the engine's own
/// descriptor, its own settings document and its own display rule, and there is no table of the
/// assistant's own describing what the catalogue already describes. The physical half of the
/// descriptor - the data chain, the index roots, the free-space root, the record count - stays
/// on the engine's side and has nowhere to leak through, which is SC-2 at the boundary rather
/// than SC-2 as a promise.
///
/// <b>Two things a descriptor cannot hold.</b> The engine's <c>ColumnDescriptor</c> has no
/// notion of a column that has to have a value, and no date-without-a-time, so
/// <see cref="ColumnDefinition.Required"/> and the difference between
/// <see cref="ColumnType.Date"/> and <see cref="ColumnType.Timestamp"/> would be lost on a round
/// trip - and a definition that does not round trip is not stored. They are kept in the
/// collection's settings document instead, which is the engine's own facility for exactly this
/// (D-4: "the engine stores both and interprets neither").
///
/// Everything in that document is namespaced, so nothing a user or a model puts in
/// <see cref="CollectionDefinition.Metadata"/> can collide with it whatever it is called. A
/// metadata value is written with a marker byte as well, because the engine's settings are
/// <c>string</c> and the contract's are <c>string?</c>, and without one a value that was nothing
/// would come back as an empty string. The old adapter had that bug.
///
/// <b>Raised rather than assumed</b> (D-2): the right home for "this column has to have a value"
/// is a field on the engine's <c>ColumnDescriptor</c>, which its own comment says is cheap -
/// "adding a field here means adding a field to a document: no binary reader changes, no
/// migration". This does it without an engine change because D-2 allows the assistant exactly
/// one, and that one is the trace collections.
/// </summary>
internal static class EngineSchema
{
    private const string UserMetadataPrefix = "u:";
    private const string ColumnExtrasPrefix = "c:";

    private const string RequiredFlag = "required";
    private const string DateFlag = "date";

    private const char ValuePresent = '=';
    private const char ValueAbsent = '~';

    public static IReadOnlyList<EngineColumn> ToEngineColumns(CollectionDefinition definition) =>
        definition.Columns.Select(static column => new EngineColumn(
            column.Name,
            EngineValues.TypeOf(column.Type),
            column.Purpose ?? string.Empty,
            column.Unique,
            column.ReadOnly,
            EngineValues.ToDocument(column.Type, column.DefaultValue))).ToList();

    /// <summary>
    /// The settings document for a collection: the caller's metadata and the two column facts
    /// the descriptor has no room for.
    /// </summary>
    public static Dictionary<string, string> ToSettings(CollectionDefinition definition)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in definition.Metadata)
        {
            settings[UserMetadataPrefix + key] = value is null ? ValueAbsent.ToString() : ValuePresent + value;
        }

        foreach (var column in definition.Columns)
        {
            var flags = new List<string>(2);
            if (column.Required) flags.Add(RequiredFlag);
            if (column.Type is ColumnType.Date) flags.Add(DateFlag);

            if (flags.Count > 0)
            {
                settings[ColumnExtrasPrefix + column.Name] = string.Join(',', flags);
            }
        }

        return settings;
    }

    public static CollectionDefinition ToDefinition(
        CollectionDescriptor descriptor,
        IReadOnlyDictionary<string, string> settings,
        string? displayRule)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (key, value) in settings)
        {
            if (!key.StartsWith(UserMetadataPrefix, StringComparison.Ordinal)) continue;

            metadata[key[UserMetadataPrefix.Length..]] = value.Length > 0 && value[0] == ValuePresent
                ? value[1..]
                : null;
        }

        var columns = descriptor.Columns
            .Select(column => ToColumnDefinition(column, Flags(settings, column.Name)))
            .ToList();

        return new CollectionDefinition(
            descriptor.Name,
            string.IsNullOrEmpty(descriptor.Description) ? null : descriptor.Description,
            columns,
            metadata,
            DisplayRule.TryCreate(displayRule));
    }

    private static ColumnDefinition ToColumnDefinition(EngineColumn column, string[] flags)
    {
        var type = EngineValues.ColumnTypeOf(column.Type, flags.Contains(DateFlag));

        return new ColumnDefinition(
            column.Name,
            type,
            string.IsNullOrEmpty(column.Description) ? null : column.Description,
            required: flags.Contains(RequiredFlag),
            unique: column.Unique,
            readOnly: column.ReadOnly,
            defaultValue: EngineValues.FromDocument(type, column.DefaultValue));
    }

    private static string[] Flags(IReadOnlyDictionary<string, string> settings, string columnName) =>
        settings.TryGetValue(ColumnExtrasPrefix + columnName, out var flags)
            ? flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : [];
}
