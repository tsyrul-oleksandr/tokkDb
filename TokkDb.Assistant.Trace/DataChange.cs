namespace TokkDb.Assistant.Trace;

/// <summary>What a change did, which decides what has to be recorded to describe it (TR-2b).</summary>
public enum ChangeKind
{
    /// <summary>A record was created. The identity and a content hash; the data itself is in storage.</summary>
    Insert = 1,

    /// <summary>A record was changed. The changed fields, before and after.</summary>
    Update,

    /// <summary>A record was removed. <b>The whole record</b>, because this is about to be the only copy.</summary>
    Delete,

    /// <summary>A collection was created.</summary>
    CollectionAdded,

    /// <summary>A collection was removed, with everything in it.</summary>
    CollectionRemoved,

    /// <summary>A field was added to a collection.</summary>
    FieldAdded,

    /// <summary>A field was renamed, retyped, or made unique.</summary>
    FieldChanged,

    /// <summary>A field was removed, with every value in it.</summary>
    FieldRemoved,

    /// <summary>A relation was created or removed.</summary>
    RelationChanged
}

/// <summary>
/// Whether a change can be taken back, decided <b>before</b> it runs (D-17).
///
/// That timing is the whole point. A person is told that something cannot be undone while they
/// can still decline it, on the confirmation card that exists for exactly this - rather than
/// months later, when they ask for it back.
/// </summary>
public enum Reversibility
{
    /// <summary>A complete inverse is kept and nothing can invalidate it. Insert and update.</summary>
    Reversible = 1,

    /// <summary>
    /// The inverse is kept and is revalidated when it is used, and may refuse. Re-inserting a
    /// deleted record can violate a uniqueness rule or a relation created since it was deleted,
    /// so an inverse that was complete when it was written may no longer be valid when it is
    /// replayed.
    /// </summary>
    ReversibleWithConditions,

    /// <summary>
    /// No complete inverse is kept. Removing a field from a wide collection needs every removed
    /// value, which past the journal's cap is not retained - so the operation is classified this
    /// way before it runs and the card says so, rather than half an inverse being kept and an
    /// undo that half-restores looking like one that worked.
    /// </summary>
    NotReversible
}

/// <summary>
/// The durable record of one change to the data (TR-2, TR-4, D-17).
///
/// <b>This is three things at once, and that is why it is separate from the diagnostics.</b> It is
/// the audit log - what happened to the data and which request did it. It is the size discipline -
/// bounded by a stated rule rather than by what happened to be convenient. And it is the undo log,
/// because compensation is computed from it.
///
/// <b>Bounded is not small.</b> A delete records the whole record, since the journal is then the
/// only copy. What bounded means is that the rule is stated: the payload is shaped by the
/// operation first and capped by size second, with large values kept as a size, a hash and a
/// preview.
///
/// <b>Committed in the same transaction as the mutation it describes</b> (TR-4). Neither can exist
/// without the other, which is what makes an audit record something other than a hopeful copy.
/// </summary>
public sealed record DataChange(
    Ulid Id,
    Ulid RequestId,
    ChangeKind Kind,
    string CollectionName,
    DateTimeOffset At,
    Reversibility Reversibility)
{
    /// <summary>The record this happened to, where it happened to one.</summary>
    public Ulid? RecordId { get; init; }

    /// <summary>
    /// What is needed to describe the change and, where the matrix allows it, to take it back.
    /// Shaped by <see cref="Kind"/>: an identity and a hash for an insert, the changed fields for
    /// an update, the whole record for a delete.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Payload { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// A hash of the record as it was written, so that "has this been touched since" stays
    /// answerable for the commonest case, which is an import (AG-11a).
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// What happened to the row this change came from, where it came from an import (IN-8a). An
    /// undo computed from a stored count alone deletes records the import never created.
    /// </summary>
    public string? Disposition { get; init; }
}
