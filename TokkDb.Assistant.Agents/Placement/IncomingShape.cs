using System.Globalization;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Placement;

/// <summary>One field of what is coming in: its name, the kind of value it holds, and a little evidence.</summary>
/// <param name="Name">What the file or the person called it.</param>
/// <param name="Kind">The kind every value of it fits (IN-1b: the wider one where they disagree).</param>
/// <param name="MajorityKind">The kind most of it is, which is what a value that is not that kind is an exception to.</param>
/// <param name="Examples">A few values, as text, for the mapping step's eyes and nothing else.</param>
/// <param name="ValueCount">How many rows have a value.</param>
/// <param name="BlankCount">How many have none.</param>
/// <param name="ExceptionLines">The lines whose value is not of the majority kind, where the file had lines.</param>
public sealed record IncomingField(
    string Name,
    ColumnType Kind,
    ColumnType MajorityKind,
    IReadOnlyList<string> Examples,
    int ValueCount,
    int BlankCount,
    IReadOnlyList<int> ExceptionLines)
{
    /// <summary>Whether the field had to widen past what most of it is.</summary>
    public bool WasWidened => Kind != MajorityKind;

    /// <summary>Whether the name says this identifies something: an id, a code, a DOI.</summary>
    public bool LooksLikeAnIdentifier
    {
        get
        {
            var normalised = CollectionPrefilter.Normalise(Name);
            return normalised is "id" or "doi" or "isbn" or "code" or "ref" or "reference" or "number" or "no" or "key"
                   || normalised.EndsWith("id", StringComparison.Ordinal)
                   || normalised.EndsWith("code", StringComparison.Ordinal)
                   || normalised.EndsWith("number", StringComparison.Ordinal);
        }
    }
}

/// <summary>One incoming row: its values by field name, typed, and where it came from.</summary>
/// <param name="Fields">The values, by incoming field name; a field the row has no value for is absent.</param>
/// <param name="LineNumber">The line of the file it came from, for IN-4's report; null for prose.</param>
/// <param name="Unreadable">Why the row could not become values at all, or null.</param>
public sealed record IncomingRow(IReadOnlyDictionary<string, object?> Fields, int? LineNumber, string? Unreadable = null);

/// <summary>
/// What is coming in, separately from where it might go (IN-5): the fields, the typed rows, and
/// what it was called - by the file, the sheet, or the extraction.
///
/// One of these per sheet of a workbook, one per CSV, one per piece of prose the extraction read.
/// It carries the rows because they are what will be written, and the model never sees them:
/// the mapping step is shown the fields and a few examples (IN-3), and the rows go straight from
/// here to the importer.
/// </summary>
public sealed record IncomingShape(
    string Name,
    string? Purpose,
    IReadOnlyList<IncomingField> Fields,
    IReadOnlyList<IncomingRow> Rows,
    string Source)
{
    public bool IsEmpty => Rows.Count == 0;

    public IReadOnlyList<string> FieldNames => [.. Fields.Select(static incoming => incoming.Name)];

    /// <summary>The fields as the mapping step is shown them: name, kind, and a couple of examples.</summary>
    public string Describe()
    {
        var lines = Fields.Select(field =>
            $"- {field.Name}: {SchemaDigest.Kind(field.Kind)}"
            + (field.WasWidened ? $" (mostly {SchemaDigest.Kind(field.MajorityKind)}, {field.ExceptionLines.Count} values are not)" : "")
            + (field.Examples.Count > 0 ? $", e.g. {string.Join(" | ", field.Examples.Take(2))}" : ""));

        return $"incoming: {Name}, {Rows.Count.ToString(CultureInfo.InvariantCulture)} rows\nfields:\n" + string.Join("\n", lines);
    }

    /// <summary>Whether a name can be a thing's: a letter, then letters, digits or underscores, at most 64 (the storage's rule).</summary>
    public static bool IsUsableThingName(string? name) =>
        name is not null && name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// A usable name from one that is not: what remains after the leading digits and underscores
    /// go ("133804_custom_campaigns_2" is "custom_campaigns_2"), or "things" when nothing does.
    /// </summary>
    public static string UsableFrom(string name)
    {
        var trimmed = new string(name.SkipWhile(static c => !char.IsAsciiLetter(c)).ToArray());
        var cleaned = new string(trimmed.Where(static c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()).TrimEnd('_');
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd('_');
        return IsUsableThingName(cleaned) ? cleaned : "things";
    }

    /// <summary>The name as a thing stored would be called: lower case, words joined by underscores.</summary>
    public static string AsThingName(string name)
    {
        var words = name.Split([' ', '-', '_', '.', '/', '\\', ',', ';', ':', '(', ')', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => word.ToLower(CultureInfo.InvariantCulture))
            .Where(static word => word.Length > 0)
            .ToList();

        var joined = string.Join("_", words);
        if (joined.Length == 0) joined = "things";
        if (joined.Length > 60) joined = joined[..60].TrimEnd('_');

        return joined;
    }

    /// <summary>A file's tables, one shape each. Values are read as the profiles concluded they should be.</summary>
    public static IReadOnlyList<IncomingShape> FromTables(IReadOnlyList<Table> tables, string fileName)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var shapes = new List<IncomingShape>();

        foreach (var table in tables)
        {
            // Headings and no rows (N-5): a lone row of names, which the parser rightly refuses to
            // call a header with nothing under it, is a table with nothing in it to keep.
            if (!table.Reading.HeaderWasFound && table.RowCount == 1
                && table.Rows[0].Cells.All(static cell => !string.IsNullOrWhiteSpace(cell) && ValueParsing.KindOf(cell) is ValueKind.Text))
            {
                shapes.Add(new IncomingShape(
                    AsThingName(fileName),
                    null,
                    [.. table.Rows[0].Cells.Select(cell => new IncomingField(AsThingName(cell!), ColumnType.Text, ColumnType.Text, [], 0, 0, []))],
                    [],
                    fileName));
                continue;
            }

            var fields = table.Profiles.Select(profile => new IncomingField(
                profile.Name,
                SchemaDigest.Type(profile.Inferred),
                SchemaDigest.Type(profile.Majority),
                profile.Examples.Take(3).ToList(),
                profile.ValueCount,
                profile.BlankCount,
                [.. profile.Exceptions.Select(static exception => exception.LineNumber)])).ToList();

            var rows = new List<IncomingRow>(table.RowCount);
            foreach (var row in table.Rows)
            {
                var values = new Dictionary<string, object?>(Names.Comparer);
                for (var column = 0; column < table.Profiles.Count; column++)
                {
                    var read = table.Profiles[column].Read(row[column]);
                    if (read is not null) values[table.Profiles[column].Name] = read;
                }

                rows.Add(new IncomingRow(values, row.LineNumber));
            }

            var name = tables.Count > 1 && !string.Equals(table.Name, fileName, StringComparison.OrdinalIgnoreCase)
                ? table.Name
                : fileName;

            shapes.Add(new IncomingShape(
                AsThingName(name),
                table.Reading.Preamble.Count > 0 ? string.Join(" ", table.Reading.Preamble) : null,
                fields,
                rows,
                tables.Count > 1 ? $"{fileName} / {table.Name}" : fileName));
        }

        return shapes;
    }

    /// <summary>
    /// What the extraction step said it found, as a shape: the fields every record shares, each
    /// value read as the kind the model said it was - and kept as text where it will not read.
    /// </summary>
    public static IncomingShape FromExtraction(string kind, IReadOnlyList<IReadOnlyList<(string Name, string Value, string Kind)>> records, string source)
    {
        ArgumentNullException.ThrowIfNull(records);

        var kinds = new Dictionary<string, List<ValueKind>>(Names.Comparer);
        var examples = new Dictionary<string, List<string>>(Names.Comparer);
        var order = new List<string>();

        foreach (var record in records)
        {
            foreach (var (name, value, word) in record)
            {
                var normalised = AsThingName(name);
                if (!kinds.ContainsKey(normalised))
                {
                    kinds[normalised] = [];
                    examples[normalised] = [];
                    order.Add(normalised);
                }

                var declared = SchemaDigest.Type(word) ?? ColumnType.Text;
                var readable = ValueParsing.TryRead(ToValueKind(declared), value, out _) ? declared : ColumnType.Text;
                kinds[normalised].Add(ToValueKind(readable));
                if (examples[normalised].Count < 3 && !string.IsNullOrWhiteSpace(value)) examples[normalised].Add(value);
            }
        }

        var fields = order.Select(name =>
        {
            var seen = kinds[name];
            var widest = Widest(seen);
            var majority = seen.GroupBy(static kind => kind).OrderByDescending(static group => group.Count()).First().Key;
            return new IncomingField(name, SchemaDigest.Type(widest), SchemaDigest.Type(majority), examples[name], seen.Count, records.Count - seen.Count, []);
        }).ToList();

        var rows = records.Select(record =>
        {
            var values = new Dictionary<string, object?>(Names.Comparer);
            foreach (var (name, value, _) in record)
            {
                var field = fields.First(candidate => Names.Same(candidate.Name, AsThingName(name)));
                if (ValueParsing.TryRead(ToValueKind(field.Kind), value, out var read) && read is not null) values[field.Name] = read;
                else if (!string.IsNullOrWhiteSpace(value)) values[field.Name] = value.Trim();
            }

            return new IncomingRow(values, null);
        }).ToList();

        return new IncomingShape(AsThingName(kind), null, fields, rows, source);
    }

    private static ValueKind ToValueKind(ColumnType type) => type switch
    {
        ColumnType.Integer => ValueKind.Integer,
        ColumnType.Decimal => ValueKind.Decimal,
        ColumnType.Boolean => ValueKind.Boolean,
        ColumnType.Date => ValueKind.Date,
        ColumnType.Timestamp => ValueKind.Timestamp,
        _ => ValueKind.Text
    };

    /// <summary>The narrowest kind that holds every value: a whole number widens to a number, a day to a moment, anything else to text (IN-1b).</summary>
    private static ValueKind Widest(IReadOnlyList<ValueKind> kinds)
    {
        var distinct = kinds.Distinct().ToList();
        if (distinct.Count == 1) return distinct[0];
        if (distinct.All(static kind => kind is ValueKind.Integer or ValueKind.Decimal)) return ValueKind.Decimal;
        if (distinct.All(static kind => kind is ValueKind.Date or ValueKind.Timestamp)) return ValueKind.Timestamp;
        return ValueKind.Text;
    }
}
