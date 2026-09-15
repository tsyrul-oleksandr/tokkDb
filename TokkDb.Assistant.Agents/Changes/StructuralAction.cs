using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Changes;

/// <summary>
/// One change to the shape of the storage, or to what it holds, as the assistant proposes it
/// (AG-3, AG-4, SC-6): the thing the classifier judges, the card describes, the executor
/// applies and the journal records. Each is data, so that a proposal can be validated, hashed,
/// shown and executed as one object (AG-3d).
/// </summary>
public abstract record StructuralAction(string Thing)
{
    /// <summary>A stored name as the person reads it: "conference expenses", never "conference_expenses" (UI-2).</summary>
    protected static string Plain(string name) => name.Replace('_', ' ');

    /// <summary>What it does, in the user's words, for the trace and the card.</summary>
    public abstract string Describe();
}

/// <summary>A new thing to store, with its fields.</summary>
public sealed record CreateThing(CollectionDefinition Definition) : StructuralAction(Definition.Name)
{
    public override string Describe() =>
        $"start keeping {Plain(Definition.Name)}, with {Definition.Columns.Count} fields: {string.Join(", ", Definition.Columns.Select(static column => column.Name))}";
}

/// <summary>A field added to a thing.</summary>
public sealed record AddField(string Thing, ColumnDefinition Column) : StructuralAction(Thing)
{
    public override string Describe() =>
        $"add a field called {Plain(Column.Name)} to {Plain(Thing)}" + (Column.Required ? ", always needed" : "") + (Column.Unique ? ", no two the same" : "");
}

/// <summary>A field made one no two records may share (IN-6b), or the rule taken off again.</summary>
public sealed record MakeUnique(string Thing, string Field, bool Unique = true) : StructuralAction(Thing)
{
    public override string Describe() => Unique
        ? $"make sure no two {Plain(Thing)} share the same {Plain(Field)}"
        : $"allow two {Plain(Thing)} to share the same {Plain(Field)}";
}

/// <summary>A field made always needed.</summary>
public sealed record MakeRequired(string Thing, string Field, bool Required = true) : StructuralAction(Thing)
{
    public override string Describe() => Required ? $"make {Plain(Field)} always needed in {Plain(Thing)}" : $"allow {Plain(Thing)} without a {Plain(Field)}";
}

/// <summary>A link from one thing to another (SC-8).</summary>
public sealed record AddRelation(RelationDefinition Relation) : StructuralAction(Relation.FromCollection)
{
    public override string Describe() =>
        $"link each of {Plain(Relation.FromCollection)} to one of {Plain(Relation.ToCollection)} by its {Relation.FromColumn}";
}

/// <summary>A field taken away, with every value in it.</summary>
public sealed record RemoveField(string Thing, string Field) : StructuralAction(Thing)
{
    public override string Describe() => $"drop the {Plain(Field)} from {Plain(Thing)}";
}

/// <summary>A field given a new name.</summary>
public sealed record RenameField(string Thing, string Field, string NewName) : StructuralAction(Thing)
{
    public override string Describe() => $"call the {Plain(Field)} of {Plain(Thing)} {Plain(NewName)} from now on";
}

/// <summary>A field made to keep a different kind of value.</summary>
public sealed record RetypeField(string Thing, string Field, ColumnType NewType) : StructuralAction(Thing)
{
    public override string Describe() => $"keep {Plain(Field)} of {Plain(Thing)} as {Context.SchemaDigest.Kind(NewType)} from now on";
}

/// <summary>A whole thing removed, with everything in it.</summary>
public sealed record RemoveThing(string Thing) : StructuralAction(Thing)
{
    public override string Describe() => $"stop keeping {Plain(Thing)} altogether";
}

/// <summary>Records removed from a thing.</summary>
public sealed record DeleteRecords(string Thing, IReadOnlyList<Ulid> Ids) : StructuralAction(Thing)
{
    public override string Describe() => Ids.Count == 1 ? $"remove one of {Plain(Thing)}" : $"remove {Ids.Count} of {Plain(Thing)}";
}

/// <summary>Records written to a thing: the additive change that happens without asking (D-7).</summary>
public sealed record StoreRecords(string Thing, int Count) : StructuralAction(Thing)
{
    public override string Describe() => Count == 1 ? $"keep one more of {Plain(Thing)}" : $"keep {Count} more of {Plain(Thing)}";
}

/// <summary>
/// One value of one record set to something else, from the browser (BR-8): the same rules as a
/// correction said in the conversation, and the same trace - the version before beside the
/// version after. The value is already of the field's kind; what does not read is refused
/// before an action exists.
/// </summary>
public sealed record ChangeRecord(string Thing, Ulid Id, string Field, object? Value) : StructuralAction(Thing)
{
    public override string Describe() => $"change the {Plain(Field)} of one of {Plain(Thing)}";
}

/// <summary>
/// Records erased for good (NF-4d, NF-4d1): the records, every version of them, and the step
/// payloads of the requests that changed them - so no reachable copy remains in the database or
/// its journal. Destructive and not reversible, and the card says that conversations are kept.
/// </summary>
public sealed record EraseRecords(string Thing, IReadOnlyList<Ulid> Ids) : StructuralAction(Thing)
{
    public override string Describe() => Ids.Count == 1 ? $"erase one of {Plain(Thing)} for good" : $"erase {Ids.Count} of {Plain(Thing)} for good";
}

/// <summary>How a record of a thing reads as a line (SC-11): Safe, since nothing stored changes.</summary>
public sealed record SetDisplayRule(string Thing, DisplayRule? Rule) : StructuralAction(Thing)
{
    public override string Describe() => Rule is null ? $"stop naming {Plain(Thing)} by a rule" : $"name each of {Plain(Thing)} as \"{Rule.Template}\"";
}
