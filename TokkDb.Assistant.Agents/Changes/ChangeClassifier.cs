using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Changes;

/// <summary>Three classes, by what a change can do and not by what it is called (D-14).</summary>
public enum ChangeClass
{
    /// <summary>No stored value can be lost and no meaning can change. Applied without asking.</summary>
    Safe = 1,

    /// <summary>Nothing is deleted, but the meaning of existing records changes or future writes can start failing. Asked.</summary>
    ReviewRequired,

    /// <summary>Stored values are lost or become unreadable. Asked, with the loss stated in counts and examples.</summary>
    Destructive
}

/// <summary>
/// What a change would do, counted before the question is asked (D-14, AG-4, UI-4): a count, what
/// it is out of, a few examples in the user's words, and one sentence saying so.
/// </summary>
public sealed record Evidence(int Count, int OutOf, IReadOnlyList<string> Examples, string Sentence)
{
    public static readonly Evidence None = new(0, 0, [], "nothing stored is affected");
}

/// <summary>
/// One action with its class, its evidence and its reversibility, all decided before it runs
/// (D-14, AG-11c, AG-11d): what the card shows and what the executor applies.
/// </summary>
public sealed record ClassifiedChange(StructuralAction Action, ChangeClass Class, Evidence Evidence, Reversibility Reversibility)
{
    public bool NeedsConfirmation => Class is not ChangeClass.Safe;

    public string Description => Action.Describe();
}

/// <summary>
/// The classifier (D-14, AG-4, AG-11c, step 4.5): every change is classified by what it can do,
/// with the evidence computed from the storage before any question is put, and its reversibility
/// decided from what the journal will be able to keep.
///
/// The rule per action, and why:
/// <list type="bullet">
/// <item><b>Safe</b>: a new thing; an optional field; a rename; a widening retype; a display rule;
/// records written. Nothing stored can be lost and nothing stored means anything different.</item>
/// <item><b>ReviewRequired</b>: a required field (every existing record becomes incomplete); a
/// unique field (writes the shape allowed can start failing, and existing collisions are named);
/// a relation (integrity is enforced from now on, and source values matching nothing are counted);
/// a narrowing retype that loses nothing today (values that will not convert: none, but the
/// meaning has changed).</item>
/// <item><b>Destructive</b>: removing a field (the records holding a value are counted and three
/// are shown); a retype that leaves values unconvertible; removing a thing; removing records, with
/// what goes with them.</item>
/// </list>
/// </summary>
public sealed class ChangeClassifier
{
    /// <summary>How many examples a card shows. Three: enough to recognise, not enough to read.</summary>
    public const int ExamplesShown = 3;

    private readonly IStorage _storage;
    private readonly PayloadLimits _limits;

    public ChangeClassifier(IStorage storage, PayloadLimits? limits = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _limits = limits ?? PayloadLimits.Default;
    }

    public ClassifiedChange Classify(StructuralAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action switch
        {
            CreateThing create => new ClassifiedChange(create, ChangeClass.Safe, Evidence.None, Reversibility.Reversible),
            AddField add => ClassifyAddField(add),
            MakeUnique unique => ClassifyUnique(unique),
            MakeRequired required => ClassifyRequired(required),
            AddRelation relation => ClassifyRelation(relation),
            RemoveField remove => ClassifyRemoveField(remove),
            RenameField rename => new ClassifiedChange(rename, ChangeClass.Safe, Evidence.None, Reversibility.Reversible),
            RetypeField retype => ClassifyRetype(retype),
            RemoveThing removeThing => ClassifyRemoveThing(removeThing),
            DeleteRecords delete => ClassifyDelete(delete),
            EraseRecords erase => ClassifyErase(erase),
            StoreRecords store => new ClassifiedChange(store, ChangeClass.Safe, Evidence.None, RecordReversibility(store.Thing)),
            ChangeRecord change => ClassifyChangeRecord(change),
            SetDisplayRule rule => new ClassifiedChange(rule, ChangeClass.Safe, Evidence.None, Reversibility.Reversible),
            _ => throw new ArgumentException($"Nothing classifies a {action.GetType().Name}.", nameof(action))
        };
    }

    /// <summary>AG-11c for a record change: from what the collection declares, before it runs.</summary>
    public Reversibility RecordReversibility(string thing)
    {
        var definition = _storage.GetCollectionDefinition(thing);
        if (definition is null) return Reversibility.Reversible;

        var unique = definition.Columns.Any(static column => column.Unique);
        var related = _storage.GetRelations().Any(relation =>
            Names.Same(relation.FromCollection, definition.Name) || Names.Same(relation.ToCollection, definition.Name));

        return Reversibilities.OfRecordChange(unique, related);
    }

    private ClassifiedChange ClassifyAddField(AddField add)
    {
        var definition = Require(add.Thing);
        var records = _storage.GetAll(definition.Name);

        if (add.Column.Required && add.Column.DefaultValue is null && records.Count > 0)
        {
            // D-14's first way "additive" was too coarse: every existing record is now incomplete.
            var examples = records.Take(ExamplesShown).Select(record => DisplayValue.For(definition, record)).ToList();
            return new ClassifiedChange(add, ChangeClass.ReviewRequired,
                new Evidence(records.Count, records.Count, examples,
                    records.Count == 1
                        ? $"the one {definition.Name} you have would be left without a {add.Column.Name}, which would then always be needed"
                        : $"all {records.Count} {definition.Name} you have would be left without a {add.Column.Name}, which would then always be needed"),
                Reversibility.Reversible);
        }

        if (add.Column.Unique)
        {
            // A new field is empty everywhere, so nothing collides today; but the shape's promise
            // to future writes changes, which is the second way.
            return new ClassifiedChange(add, ChangeClass.ReviewRequired,
                new Evidence(0, records.Count, [], $"from now on no two {definition.Name} may share the same {add.Column.Name}"),
                Reversibility.Reversible);
        }

        return new ClassifiedChange(add, ChangeClass.Safe, Evidence.None, Reversibility.Reversible);
    }

    private ClassifiedChange ClassifyUnique(MakeUnique unique)
    {
        var definition = Require(unique.Thing);

        if (!unique.Unique)
        {
            return new ClassifiedChange(unique, ChangeClass.ReviewRequired,
                new Evidence(0, 0, [], $"two {definition.Name} could then share the same {unique.Field}"),
                Reversibility.Reversible);
        }

        var effect = _storage.InspectUnique(definition.Name, unique.Field);
        var examples = effect.Collisions.Take(ExamplesShown)
            .Select(group => string.Join(" and ", group.Take(2).Select(reference => Title(definition, reference.Id))))
            .ToList();

        return new ClassifiedChange(unique, ChangeClass.ReviewRequired,
            new Evidence(effect.CollidingRecords, effect.RecordsInspected, examples,
                effect.IsPossible
                    ? $"no two of your {effect.RecordsInspected} {definition.Name} share a {unique.Field} today, and from now on none may"
                    : $"{effect.CollidingRecords} of your {effect.RecordsInspected} {definition.Name} share a {unique.Field} with another, and the rule cannot be made until they are sorted out"),
            Reversibility.Reversible);
    }

    private ClassifiedChange ClassifyRequired(MakeRequired required)
    {
        var definition = Require(required.Thing);
        var records = _storage.GetAll(definition.Name);
        var missing = records.Where(record => record[required.Field] is null).ToList();

        return new ClassifiedChange(required, ChangeClass.ReviewRequired,
            new Evidence(missing.Count, records.Count, missing.Take(ExamplesShown).Select(record => DisplayValue.For(definition, record)).ToList(),
                missing.Count == 0
                    ? $"every one of your {records.Count} {definition.Name} already has a {required.Field}"
                    : $"{missing.Count} of your {records.Count} {definition.Name} have no {required.Field} and would count as incomplete"),
            Reversibility.Reversible);
    }

    private ClassifiedChange ClassifyRelation(AddRelation relation)
    {
        var from = Require(relation.Relation.FromCollection);
        var records = _storage.GetAll(from.Name);
        var values = records.Select(record => record[relation.Relation.FromColumn]).Where(static value => value is not null).ToList();

        var matched = values.Count == 0
            ? null
            : _storage.MatchValues(relation.Relation.ToCollection, relation.Relation.ToColumn, values);

        var unmatched = matched is null ? [] : records
            .Where(record => record[relation.Relation.FromColumn] is { } value && !matched.Contains(value))
            .ToList();

        return new ClassifiedChange(relation, ChangeClass.ReviewRequired,
            new Evidence(unmatched.Count, values.Count, unmatched.Take(ExamplesShown).Select(record => DisplayValue.For(from, record)).ToList(),
                unmatched.Count == 0
                    ? $"every {from.Name} with a {relation.Relation.FromColumn} points at one of {relation.Relation.ToCollection}"
                    : $"{unmatched.Count} of your {values.Count} {from.Name} name a {relation.Relation.FromColumn} that matches none of {relation.Relation.ToCollection}"),
            Reversibility.Reversible);
    }

    private ClassifiedChange ClassifyRemoveField(RemoveField remove)
    {
        var definition = Require(remove.Thing);
        var column = definition.Column(remove.Field) ?? throw new UnknownColumnException(definition.Name, remove.Field);
        var records = _storage.GetAll(definition.Name);
        var holding = records.Where(record => record[column.Name] is not null).ToList();

        // AG-11c, AG-11e: reversible only while the journal can keep every removed value whole.
        var payload = DataChanges.Structural(Ulid.Empty, ChangeKind.FieldRemoved, definition.Name,
            [.. holding.Select(record => FieldChange.Removed(record.Id.ToString(), JournalValue.Of(record[column.Name], _limits)))],
            Reversibility.ReversibleWithConditions, _limits);

        var examples = holding.Take(ExamplesShown)
            .Select(record => $"{DisplayValue.For(definition, record)}: {Render(record[column.Name])}")
            .ToList();

        return new ClassifiedChange(remove, ChangeClass.Destructive,
            new Evidence(holding.Count, records.Count, examples,
                holding.Count == 0
                    ? $"none of your {records.Count} {definition.Name} has a {column.Name}, so nothing is lost"
                    : $"{holding.Count} of your {records.Count} {definition.Name} have a {column.Name}, and those values would go"),
            holding.Count == 0 ? Reversibility.Reversible : payload.Reversibility);
    }

    private ClassifiedChange ClassifyRetype(RetypeField retype)
    {
        var definition = Require(retype.Thing);
        var effect = _storage.InspectRetype(definition.Name, retype.Field, retype.NewType);

        if (effect.IsLossless)
        {
            return new ClassifiedChange(retype, ChangeClass.Safe, Evidence.None,
                effect.From == retype.NewType ? Reversibility.Reversible : Reversibility.ReversibleWithConditions);
        }

        var examples = effect.WillNotConvert.Take(ExamplesShown)
            .Select(value => $"{Title(definition, value.RecordId)}: {Render(value.Value)}")
            .ToList();

        if (effect.LossCount == 0)
        {
            return new ClassifiedChange(retype, ChangeClass.ReviewRequired,
                new Evidence(0, effect.ValuesInspected, [],
                    $"every one of the {effect.ValuesInspected} values of {retype.Field} reads as {SchemaDigest.Kind(retype.NewType)}, and from now on only such values are kept"),
                Reversibility.ReversibleWithConditions);
        }

        return new ClassifiedChange(retype, ChangeClass.Destructive,
            new Evidence(effect.LossCount, effect.ValuesInspected, examples,
                $"{effect.LossCount} of the {effect.ValuesInspected} values of {retype.Field} cannot be read as {SchemaDigest.Kind(retype.NewType)} and would be left needing attention"),
            Reversibility.ReversibleWithConditions);
    }

    private ClassifiedChange ClassifyRemoveThing(RemoveThing remove)
    {
        var definition = Require(remove.Thing);
        var records = _storage.GetAll(definition.Name);
        var referring = _storage.GetRelations().Where(relation => Names.Same(relation.ToCollection, definition.Name)).ToList();

        return new ClassifiedChange(remove, ChangeClass.Destructive,
            new Evidence(records.Count, records.Count, records.Take(ExamplesShown).Select(record => DisplayValue.For(definition, record)).ToList(),
                (records.Count == 0 ? $"there are no {definition.Name} to lose" : $"all {records.Count} {definition.Name} would go, with everything they keep")
                + (referring.Count == 0 ? "" : $"; {string.Join(" and ", referring.Select(static relation => relation.FromCollection))} would no longer point at anything")),
            Reversibility.NotReversible);
    }

    /// <summary>
    /// A value changed keeps its previous version, so nothing is lost and nothing is asked (D-7),
    /// as a correction said in the conversation is not asked about either. The evidence names the
    /// record and what the field reads now, so the trace shows the old value beside the new.
    /// </summary>
    private ClassifiedChange ClassifyChangeRecord(ChangeRecord change)
    {
        var definition = Require(change.Thing);
        var column = definition.Column(change.Field) ?? throw new UnknownColumnException(definition.Name, change.Field);
        var record = _storage.GetById(definition.Name, change.Id);
        if (record is null)
        {
            return new ClassifiedChange(change, ChangeClass.Safe, new Evidence(0, 0, [], "it is no longer there, so nothing would change"), Reversibility.Reversible);
        }

        var title = DisplayValue.For(definition, record);
        var before = Orchestration.Values.Show(record[column.Name]);
        var after = Orchestration.Values.Show(change.Value);
        var sentence = record[column.Name] is null
            ? $"{Plain(column.Name)} of {title} is empty and would read {after}"
            : $"{Plain(column.Name)} of {title} reads {before} and would read {after}";

        return new ClassifiedChange(change, ChangeClass.Safe, new Evidence(1, 1, [title], sentence), RecordReversibility(definition.Name));
    }

    private static string Plain(string name) => name.Replace('_', ' ');

    /// <summary>NF-4d: erasing is a deletion with no way back, said in counts and examples like any other loss.</summary>
    private ClassifiedChange ClassifyErase(EraseRecords erase)
    {
        var definition = Require(erase.Thing);
        var examples = new List<string>();
        var existing = 0;
        foreach (var id in erase.Ids)
        {
            if (_storage.GetById(definition.Name, id) is null) continue;
            existing++;
            if (examples.Count < ExamplesShown) examples.Add(Title(definition, id));
        }

        var sentence = existing == 1
            ? $"one of your {Plain(definition.Name)} would be gone for good, with everything it ever read; what you said about it in conversations is kept"
            : $"{existing} of your {Plain(definition.Name)} would be gone for good, with everything they ever read; what you said about them in conversations is kept";

        return new ClassifiedChange(erase, ChangeClass.Destructive, new Evidence(existing, existing, examples, sentence), Reversibility.NotReversible);
    }

    private ClassifiedChange ClassifyDelete(DeleteRecords delete)
    {
        var definition = Require(delete.Thing);
        var alsoRemoved = 0;
        var blocked = new List<string>();
        var examples = new List<string>();

        foreach (var id in delete.Ids)
        {
            var effect = _storage.InspectDelete(definition.Name, id);
            if (!effect.RecordExists) continue;

            alsoRemoved += effect.WouldAlsoBeRemoved.Count;
            if (examples.Count < ExamplesShown) examples.Add(Title(definition, id));

            foreach (var blocker in effect.Blocking.Take(ExamplesShown))
            {
                blocked.Add($"{Title(definition, id)} is still pointed at by {Title(_storage.GetCollectionDefinition(blocker.CollectionName)!, blocker.Id)}");
            }
        }

        var sentence = delete.Ids.Count == 1
            ? $"one of your {Plain(definition.Name)} would go"
            : $"{delete.Ids.Count} of your {Plain(definition.Name)} would go";
        if (alsoRemoved > 0) sentence += $", and {alsoRemoved} other records that belong to them with them";
        if (blocked.Count > 0) sentence += $"; it cannot happen while {string.Join("; ", blocked)}";

        return new ClassifiedChange(delete, ChangeClass.Destructive,
            new Evidence(delete.Ids.Count + alsoRemoved, _storage.GetAll(definition.Name).Count, examples, sentence),
            RecordReversibility(definition.Name));
    }

    private CollectionDefinition Require(string thing) =>
        _storage.GetCollectionDefinition(thing) ?? throw new UnknownCollectionException(thing);

    private string Title(CollectionDefinition definition, Ulid id) =>
        _storage.GetById(definition.Name, id) is { } record ? DisplayValue.For(definition, record) : DisplayValue.Shortened(id);

    private static string Render(object? value) => value switch
    {
        null => "nothing",
        string text => text.Length <= 40 ? $"\"{text}\"" : $"\"{text[..39]}…\"",
        bool flag => flag ? "yes" : "no",
        DateOnly day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}
