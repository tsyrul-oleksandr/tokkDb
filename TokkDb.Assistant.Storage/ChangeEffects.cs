namespace TokkDb.Assistant.Storage;

/// <summary>One record, named well enough to fetch it or to show it.</summary>
public readonly record struct RecordReference(string CollectionName, Ulid Id)
{
    public override string ToString() => $"{CollectionName}/{Id}";
}

/// <summary>
/// One stored value that is not a value of its column's type.
///
/// SC-6b: it is kept rather than dropped, so this is what the caller is shown - the record it is
/// in, the column it is in, and the value itself, still in the type it was recorded as. The
/// browser marks it as needing attention and a person decides what it should be.
/// </summary>
public sealed record UnconvertibleValue(Ulid RecordId, string ColumnName, object? Value, ColumnType Recorded)
{
    public override string ToString() =>
        $"{RecordId}.{ColumnName} holds {ColumnTypes.Render(Value)}, which is {Recorded}";
}

/// <summary>
/// What retyping a column would do, counted before it is proposed.
///
/// SC-6a: widening is lossless and needs no evidence, and every other direction is a change
/// under D-14 whose evidence is the count of stored values that will not convert. "This would
/// leave 3 of your 47 records without a cost" is answerable; "this is a narrowing conversion" is
/// not, and a card that says the second one is the nagging D-7 exists to avoid.
/// </summary>
public sealed record RetypeEffect(
    string CollectionName,
    string ColumnName,
    ColumnType From,
    ColumnType To,
    bool IsLossless,
    int ValuesInspected,
    IReadOnlyList<UnconvertibleValue> WillNotConvert)
{
    /// <summary>How many stored values have no meaning under the new type.</summary>
    public int LossCount => WillNotConvert.Count;

    public override string ToString() =>
        IsLossless
            ? $"{CollectionName}.{ColumnName}: {From} to {To}, nothing can be lost"
            : $"{CollectionName}.{ColumnName}: {From} to {To}, {LossCount} of {ValuesInspected} values " +
              $"would stop being {To}";
}

/// <summary>
/// What making a column unique would do, counted before it is proposed.
///
/// IN-6b: accepting a natural key creates the uniqueness rule that makes it one, and a
/// collection that already holds two records with the same value has to say so here rather than
/// fail at the first write of an import that is already half done.
/// </summary>
public sealed record UniquenessEffect(
    string CollectionName,
    string ColumnName,
    int RecordsInspected,
    IReadOnlyList<IReadOnlyList<RecordReference>> Collisions)
{
    /// <summary>Whether the column can be made unique as the records stand.</summary>
    public bool IsPossible => Collisions.Count == 0;

    /// <summary>How many records are in a collision with another.</summary>
    public int CollidingRecords => Collisions.Sum(static group => group.Count);

    public override string ToString() =>
        IsPossible
            ? $"{CollectionName}.{ColumnName} holds {RecordsInspected} values and no two are the same"
            : $"{CollectionName}.{ColumnName} has {Collisions.Count} values held by more than one record";
}

/// <summary>
/// What deleting one record would do to the records that refer to it.
///
/// SC-8 and D-14: the counts are computed before the question is asked, never after. A card that
/// says "four expenses point at this conference, and here they are" is one a person can answer;
/// "this would violate referential integrity" is one they cannot.
/// </summary>
public sealed record DeletionEffect(
    bool RecordExists,
    IReadOnlyList<RecordReference> Blocking,
    IReadOnlyList<RecordReference> WouldAlsoBeRemoved,
    IReadOnlyList<RecordReference> WouldBeCleared)
{
    /// <summary>Whether something refers to it under a rule that refuses the delete.</summary>
    public bool IsRefused => Blocking.Count > 0;

    public static DeletionEffect Nothing(bool exists) => new(exists, [], [], []);
}

/// <summary>
/// What deleting one record actually did.
///
/// The lists are here because SC-8a requires each cascaded deletion to be its own change record:
/// a caller that is told only "deleted" cannot record five deletions, and an undo computed from
/// that would restore one record and leave four gone.
/// </summary>
public sealed record DeletionResult(
    bool Removed,
    IReadOnlyList<RecordReference> AlsoRemoved,
    IReadOnlyList<RecordReference> Cleared)
{
    public static readonly DeletionResult NotFound = new(false, [], []);

    /// <summary>Everything that went, the record itself included - what a compensation has to put back.</summary>
    public int TotalRemoved => (Removed ? 1 : 0) + AlsoRemoved.Count;
}

/// <summary>
/// What converging a collection did, and what it could not do.
///
/// SC-6b: converge reports what it could not convert rather than silently finishing, and is
/// idempotent - run twice, it reports the same set the second time and rewrites nothing.
/// </summary>
public sealed record ConvergeReport(
    string CollectionName,
    int RecordsBroughtUp,
    IReadOnlyList<UnconvertibleValue> CouldNotConvert)
{
    public static ConvergeReport Nothing(string collectionName) => new(collectionName, 0, []);

    public override string ToString() =>
        CouldNotConvert.Count == 0
            ? $"{CollectionName}: {RecordsBroughtUp} records brought up to date"
            : $"{CollectionName}: {RecordsBroughtUp} records brought up to date, " +
              $"{CouldNotConvert.Count} values left as they were";
}

/// <summary>
/// One thing as the overview lists it (BR-1): the definition, how many records it holds, and when
/// it last changed - a write to a record, a change to its shape, or its creation. Both figures
/// are maintained with the writes (BR-1a) and read here without touching a record.
/// </summary>
public sealed record StoredThing(CollectionDefinition Definition, long RecordCount, DateTimeOffset? LastChanged)
{
    public string Name => Definition.Name;
}
