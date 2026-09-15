using System.Globalization;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Placement;

/// <summary>What the mapping operation answered, parsed: a choice from the shortlist or "none", and its field mappings.</summary>
/// <param name="Choice">One of the offered names, or "none".</param>
/// <param name="NewName">A name for a new thing, when the choice is "none".</param>
/// <param name="Purpose">One sentence about what it keeps.</param>
/// <param name="Fields">Incoming field to existing field, with an empty existing for "nothing fits".</param>
public sealed record MappingAnswer(string Choice, string? NewName, string? Purpose, IReadOnlyList<(string Incoming, string Existing)> Fields)
{
    public bool IsNone => string.Equals(Choice, "none", StringComparison.OrdinalIgnoreCase) || Choice.Length == 0;
}

/// <summary>
/// Builds the placement proposal (AG-3, AG-3a, AG-3b, AG-3c, IN-6, IN-6b, step 4.4).
///
/// <b>The model ranks and corrects a shortlist C# produced, and C# decides how sure it is.</b> The
/// model's choice is taken only from what was offered; its field mappings are checked against the
/// target and completed by name where it left gaps; the confidence is computed here from name
/// overlap after normalisation, per-field type compatibility, the unmapped incoming fields, the
/// unfilled required fields and whether a natural key matched. What the model says about its own
/// confidence is never read.
/// </summary>
public sealed class Placer
{
    private readonly IStorage _storage;
    private readonly ChangeClassifier _classifier;

    public Placer(IStorage storage, ChangeClassifier classifier)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    /// <summary>
    /// The proposal for an incoming shape, given the shortlist and what the model answered about
    /// it - or nothing, when the shortlist was empty and no model was asked.
    /// </summary>
    public PlacementProposal Propose(IncomingShape shape, IReadOnlyList<PlacementCandidate> shortlist, MappingAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(shortlist);

        var schema = SchemaVersion.Of(_storage);

        PlacementCandidate? chosen = answer is null || answer.IsNone
            ? null
            : shortlist.FirstOrDefault(candidate => Names.Same(candidate.Definition.Name, answer.Choice));

        // The correction half of AG-3c, from step 0.4's measurement: the commonest wrong answer is
        // "new" with the very name that was offered. That is a choice of the offered thing with the
        // wrong flag, and is read as the choice.
        if (chosen is null && answer is { IsNone: true, NewName: { Length: > 0 } proposedName })
        {
            var normalised = IncomingShape.AsThingName(proposedName);
            chosen = shortlist.FirstOrDefault(candidate => Names.Same(IncomingShape.AsThingName(candidate.Definition.Name), normalised));
        }

        if (chosen is null)
        {
            return ProposeNew(shape, shortlist, answer, schema);
        }

        var proposal = ProposeExisting(shape, chosen, answer!, shortlist, schema);

        // The runner-up, scored the same way, by name matching alone: what the margin is over.
        var runnerUp = shortlist
            .Where(candidate => !ReferenceEquals(candidate, chosen))
            .Select(candidate => (Candidate: candidate, Proposal: ProposeExisting(shape, candidate, new MappingAnswer(candidate.Definition.Name, null, null, []), shortlist, schema)))
            .OrderByDescending(static entry => entry.Proposal.Confidence.Score)
            .FirstOrDefault();

        return runnerUp.Candidate is null
            ? proposal
            : proposal with
            {
                Confidence = proposal.Confidence with
                {
                    RunnerUp = runnerUp.Proposal.Confidence.Score,
                    RunnerUpName = runnerUp.Candidate.Definition.Name
                }
            };
    }

    /// <summary>The rows as the importer takes them: by the target's field names, typed as the target keeps them.</summary>
    public static IReadOnlyList<ImportRow> Rows(IncomingShape shape, PlacementProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(proposal);

        var targets = new Dictionary<string, (string Column, ColumnType Type)>(Names.Comparer);
        foreach (var mapping in proposal.Mappings) targets[mapping.Incoming] = (mapping.Existing, mapping.ExistingKind);
        foreach (var proposed in proposal.NewFields) targets[proposed.Incoming] = (proposed.Column.Name, proposed.Column.Type);

        var rows = new List<ImportRow>(shape.Rows.Count);

        foreach (var row in shape.Rows)
        {
            if (row.Unreadable is not null)
            {
                rows.Add(new ImportRow(new Dictionary<string, object?>(Names.Comparer), row.LineNumber, row.Unreadable));
                continue;
            }

            var fields = new Dictionary<string, object?>(Names.Comparer);
            string? unreadable = null;

            foreach (var (incoming, value) in row.Fields)
            {
                if (!targets.TryGetValue(incoming, out var target)) continue;

                if (Convert(value, target.Type, out var converted))
                {
                    fields[target.Column] = converted;
                }
                else
                {
                    // IN-4: the row is reported with its line and reason, and the rest are stored.
                    unreadable ??= $"{Render(value)} in {incoming} is not {SchemaDigest.Kind(target.Type)}";
                }
            }

            rows.Add(new ImportRow(fields, row.LineNumber, unreadable));
        }

        return rows;
    }

    private PlacementProposal ProposeNew(IncomingShape shape, IReadOnlyList<PlacementCandidate> shortlist, MappingAnswer? answer, string schema)
    {
        var name = answer?.NewName is { Length: > 0 } proposed ? IncomingShape.AsThingName(proposed) : shape.Name;
        if (_storage.GetCollectionDefinition(name) is not null) name = Unused(name);

        var purpose = answer?.Purpose is { Length: > 0 } sentence ? sentence.Trim() : shape.Purpose ?? $"{name.Replace('_', ' ')} you keep";

        var key = NaturalKey(shape, null);
        var columns = shape.Fields.Select(incoming => new ColumnDefinition(
            IncomingShape.AsThingName(incoming.Name),
            incoming.Kind,
            unique: key is not null && Names.Same(key, incoming.Name))).ToList();

        var definition = new CollectionDefinition(name, purpose, columns);
        var actions = new List<ClassifiedChange>
        {
            _classifier.Classify(new CreateThing(definition)),
            _classifier.Classify(new StoreRecords(name, shape.Rows.Count))
        };

        // How sure "nothing here fits" is: one minus the best that anything existing scores.
        var best = shortlist.Count == 0
            ? null
            : shortlist.Select(candidate => ProposeExisting(shape, candidate, new MappingAnswer(candidate.Definition.Name, null, null, []), shortlist, schema))
                .OrderByDescending(static candidate => candidate.Confidence.Score)
                .First();

        var evidence = new List<string>
        {
            shortlist.Count == 0
                ? "nothing stored has fields in common with this"
                : $"the closest thing stored, {best!.Target}, accounts for {best.Confidence.Score:P0} of the incoming fields"
        };
        if (key is not null) evidence.Add($"{key} identifies each row");

        return new PlacementProposal(
            name,
            IsNew: true,
            purpose,
            [],
            [.. shape.Fields.Select((incoming, index) => new ProposedField(incoming.Name, columns[index]))],
            [],
            actions,
            new Confidence(Math.Round(1 - (best?.Confidence.Score ?? 0), 3), evidence, best?.Confidence.Score, best?.Target),
            shortlist.Count == 0 ? $"nothing stored looks like {name}, so a new thing is made" : $"nothing stored fits {name} well enough",
            key is null ? null : IncomingShape.AsThingName(key),
            schema,
            shortlist);
    }

    private PlacementProposal ProposeExisting(IncomingShape shape, PlacementCandidate chosen, MappingAnswer answer, IReadOnlyList<PlacementCandidate> shortlist, string schema)
    {
        var target = chosen.Definition;
        var columns = target.Columns.Where(static column => !Names.Same(column.Name, Fingerprints.ColumnName)).ToList();
        var mappings = new List<FieldMapping>();
        var newFields = new List<ProposedField>();
        var filled = new HashSet<string>(Names.Comparer);

        // First what the model said, checked; then what it left, matched by name here.
        foreach (var incoming in shape.Fields)
        {
            var said = answer.Fields.FirstOrDefault(entry => Names.Same(entry.Incoming, incoming.Name) || Names.Same(IncomingShape.AsThingName(entry.Incoming), IncomingShape.AsThingName(incoming.Name)));
            var column = said.Existing is { Length: > 0 } ? columns.FirstOrDefault(candidate => Names.Same(candidate.Name, said.Existing)) : null;

            column ??= columns.FirstOrDefault(candidate => !filled.Contains(candidate.Name) && CollectionPrefilter.Same(candidate.Name, incoming.Name));

            if (column is null || filled.Contains(column.Name) || Compatibility(incoming, column) is not { } kind)
            {
                var name = IncomingShape.AsThingName(incoming.Name);
                if (target.Column(name) is not null || newFields.Any(proposed => Names.Same(proposed.Column.Name, name))) name = name + "_2";
                newFields.Add(new ProposedField(incoming.Name, new ColumnDefinition(name, incoming.Kind)));
                continue;
            }

            filled.Add(column.Name);
            mappings.Add(new FieldMapping(incoming.Name, incoming.Kind, column.Name, column.Type, kind));
        }

        var unfilled = columns.Where(column => !filled.Contains(column.Name)).Select(static column => column.Name).ToList();
        var required = columns.Where(static column => column.Required).ToList();
        var unfilledRequired = required.Count(column => !filled.Contains(column.Name));

        var key = NaturalKey(shape, target, mappings);

        var actions = new List<ClassifiedChange>();
        foreach (var proposed in newFields) actions.Add(_classifier.Classify(new AddField(target.Name, proposed.Column)));
        if (key is not null && target.Column(key) is { Unique: false }) actions.Add(_classifier.Classify(new MakeUnique(target.Name, key)));
        actions.Add(_classifier.Classify(new StoreRecords(target.Name, shape.Rows.Count)));

        // AG-3a: the evidence, and the score from it.
        var overlap = shape.Fields.Count == 0 ? 0 : (double)mappings.Count / shape.Fields.Count;
        var compatible = mappings.Count == 0 ? 0 : mappings.Sum(mapping => mapping.Kind is MappingKind.Coerced ? 0.5 : 1.0) / mappings.Count;
        var unmappedShare = shape.Fields.Count == 0 ? 0 : (double)newFields.Count / shape.Fields.Count;
        var unfilledShare = required.Count == 0 ? 0 : (double)unfilledRequired / required.Count;
        var keyMatched = key is not null;

        var score = Math.Round(0.5 * overlap + 0.2 * compatible + 0.15 * (1 - unmappedShare) + 0.1 * (1 - unfilledShare) + 0.05 * (keyMatched ? 1 : 0), 3);

        var evidence = new List<string>
        {
            $"{mappings.Count} of {shape.Fields.Count} incoming fields match a field of {target.Name} by name",
            $"{mappings.Count(static mapping => mapping.Kind is not MappingKind.Coerced)} of {mappings.Count} matched fields keep the same kind of value",
            $"{newFields.Count} incoming fields would be new",
            $"{unfilledRequired} of {required.Count} always-needed fields would be left empty",
            keyMatched ? $"{key} identifies each row" : "nothing identifies a row, so the whole row does"
        };

        return new PlacementProposal(
            target.Name,
            IsNew: false,
            target.Purpose,
            mappings,
            newFields,
            unfilled,
            actions,
            new Confidence(score, evidence, null, null),
            $"{shape.Name} looks like {target.Name}: {mappings.Count} fields in common",
            key,
            schema,
            shortlist);
    }

    /// <summary>
    /// Whether an incoming field can fill an existing one, and how: the same kind, a lossless
    /// widening, or mostly the right kind with the rest reported row by row (IN-1b, IN-4).
    /// </summary>
    private static MappingKind? Compatibility(IncomingField incoming, ColumnDefinition column)
    {
        var sameName = Names.Same(IncomingShape.AsThingName(incoming.Name), IncomingShape.AsThingName(column.Name));

        if (incoming.Kind == column.Type) return sameName ? MappingKind.Exact : MappingKind.Renamed;
        if (Widens(incoming.Kind, column.Type)) return MappingKind.Coerced;
        if (incoming.WasWidened && (incoming.MajorityKind == column.Type || Widens(incoming.MajorityKind, column.Type))) return MappingKind.Coerced;
        if (column.Type is ColumnType.Text) return MappingKind.Coerced;

        return null;
    }

    private static bool Widens(ColumnType from, ColumnType to) =>
        (from is ColumnType.Integer && to is ColumnType.Decimal) || (from is ColumnType.Date && to is ColumnType.Timestamp) || to is ColumnType.Text;

    /// <summary>
    /// IN-6: a natural key, proposed from the shape and confirmed deterministically - unique and
    /// non-null across the incoming rows, and against what is stored where the target exists.
    /// Nobody is asked; a candidate that fails is simply not the key.
    /// </summary>
    private string? NaturalKey(IncomingShape shape, CollectionDefinition? target, IReadOnlyList<FieldMapping>? mappings = null)
    {
        foreach (var incoming in shape.Fields.Where(static incoming => incoming.LooksLikeAnIdentifier))
        {
            var values = shape.Rows.Where(static row => row.Unreadable is null).Select(row => row.Fields.GetValueOrDefault(incoming.Name)).ToList();
            if (values.Count == 0 || values.Any(static value => value is null)) continue;
            if (values.Select(static value => value is string text ? text.Trim().ToLowerInvariant() : value!).Distinct().Count() != values.Count) continue;

            if (target is null) return incoming.Name;

            var mapped = mappings?.FirstOrDefault(mapping => Names.Same(mapping.Incoming, incoming.Name));
            if (mapped is null) continue;

            // Against the records already stored (IN-6): a collision means it is not a key, unless
            // it is already the unique column, in which case a match is a row already stored.
            var column = target.Column(mapped.Existing)!;
            if (column.Unique) return column.Name;

            var stored = _storage.MatchValues(target.Name, column.Name, values);
            if (stored.Found.Count > 0) continue;
            if (!_storage.InspectUnique(target.Name, column.Name).IsPossible) continue;

            return column.Name;
        }

        return null;
    }

    private string Unused(string name)
    {
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{name}_{suffix}";
            if (_storage.GetCollectionDefinition(candidate) is null) return candidate;
        }

        return name + "_" + Guid.NewGuid().ToString("N")[..6];
    }

    private static bool Convert(object? value, ColumnType type, out object? converted)
    {
        converted = value;
        if (value is null) return true;

        switch (type)
        {
            case ColumnType.Text:
                converted = value is string text ? text : JournalText(value);
                return true;
            case ColumnType.Integer:
                if (value is long) return true;
                if (value is int or short or byte) { converted = System.Convert.ToInt64(value, CultureInfo.InvariantCulture); return true; }
                if (value is decimal exact && exact == Math.Floor(exact)) { converted = (long)exact; return true; }
                return Parse(ValueKind.Integer, value, out converted);
            case ColumnType.Decimal:
                if (value is decimal) return true;
                if (value is long or int) { converted = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true; }
                return Parse(ValueKind.Decimal, value, out converted);
            case ColumnType.Boolean:
                if (value is bool) return true;
                return Parse(ValueKind.Boolean, value, out converted);
            case ColumnType.Date:
                if (value is DateOnly) return true;
                if (value is DateTime moment) { converted = DateOnly.FromDateTime(moment); return true; }
                return Parse(ValueKind.Date, value, out converted);
            case ColumnType.Timestamp:
                if (value is DateTime) return true;
                if (value is DateOnly day) { converted = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); return true; }
                return Parse(ValueKind.Timestamp, value, out converted);
            default:
                return false;
        }
    }

    private static bool Parse(ValueKind kind, object value, out object? converted)
    {
        if (value is string text && ValueParsing.TryRead(kind, text, out converted)) return true;
        converted = null;
        return false;
    }

    private static string JournalText(object value) => value switch
    {
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static string Render(object? value) => value is null ? "nothing" : $"\"{JournalText(value)}\"";
}
