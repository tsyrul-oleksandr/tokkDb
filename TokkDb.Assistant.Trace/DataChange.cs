namespace TokkDb.Assistant.Trace;

/// <summary>What a change did, which decides what has to be recorded to describe it (TR-2b).</summary>
public enum ChangeKind
{
    /// <summary>A record was created. The record and the version it produced; the data is in storage and its history.</summary>
    Insert = 1,

    /// <summary>A record was changed. The version it replaced and the version it produced.</summary>
    Update,

    /// <summary>A record was removed. The version it replaced and the tombstone; the record's last state is kept by the engine's history.</summary>
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
    /// undo that half-restores looking like one that worked. A record change becomes this only
    /// once the version it would restore has been purged from history (D-17, AJ-5).
    /// </summary>
    NotReversible
}

/// <summary>
/// AJ-5 and V-18: what a record change's reversibility is, decided before it runs from what its
/// collection declares. <see cref="Reversibility.Reversible"/> when the collection has no unique
/// column and takes part in no relation in either direction - nothing can then invalidate the
/// undo - and <see cref="Reversibility.ReversibleWithConditions"/> otherwise, because putting a
/// value back, or taking an inserted record away, can collide with a value or a reference taken
/// since. Neither depends on the width of the record or on any payload cap.
/// </summary>
public static class Reversibilities
{
    public static Reversibility OfRecordChange(bool hasUniqueColumn, bool takesPartInRelation) =>
        hasUniqueColumn || takesPartInRelation
            ? Reversibility.ReversibleWithConditions
            : Reversibility.Reversible;
}

/// <summary>
/// The durable record of one change to the data (TR-2, TR-4, D-17, V-18).
///
/// <b>This is three things at once, and that is why it is separate from the diagnostics.</b> It is
/// the audit log - what happened to the data and which request did it. It is the size discipline -
/// bounded by a stated rule rather than by what happened to be convenient. And it is the undo log,
/// because compensation is computed from it.
///
/// <b>A record change is a fixed-size set of references</b> (TR-2b, AJ-1): the request, the
/// record, the version it replaced and the version it produced, and no field values. The old
/// values live in the engine's version history, where an undo restores them and where the
/// before-and-after table is read from (TR-6), and a version answers "touched since" exactly
/// (AG-11a). <b>A structural change keeps the shaped and bounded payload</b>: the journal is
/// then the only copy of what it removed, so the operation decides what is recorded and the cap
/// decides how much of it survives, with large values kept as a size, a hash and a preview.
/// <see cref="DataChanges"/> is that rule, written once so that no caller can shape a payload
/// its own way.
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
    /// For a record change: the version the change replaced, which an undo restores (D-17).
    /// Null for an insert, which an undo deletes, and null on a change written before version
    /// references existed (step 9.1 of the versioning plan), which then cannot be undone from here.
    /// </summary>
    public Ulid? PreviousVersionId { get; init; }

    /// <summary>
    /// For a record change: the version the change produced - for a delete, the tombstone. "Has
    /// this been touched since" is whether the record's head is still this (AG-11a). Null on a
    /// change written before version references existed.
    /// </summary>
    public Ulid? VersionId { get; init; }

    /// <summary>
    /// For a structural change only: what changed, field by field, old beside new (TR-2a),
    /// shaped by <see cref="Kind"/> and bounded by <see cref="PayloadLimits"/>. Empty for a
    /// record change, whose values are in the engine's version history (V-18).
    /// </summary>
    public IReadOnlyList<FieldChange> Fields { get; init; } = [];

    /// <summary>
    /// Kept for documents written before step 9.1 of the versioning plan, when a record change
    /// carried a hash of the record as written; a change written since carries none, because
    /// the version references answer "touched since" exactly where the hash could not (V-18).
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// The step that made this change, where the diagram still has one.
    ///
    /// <b>The join that matters is <see cref="RequestId"/></b> (TR-2): it is the one that has to
    /// survive a purge, because the change outlives the diagnostics that describe it. This is the
    /// finer link, from the block a person clicked to what it did, and it is allowed to dangle -
    /// a detail panel for a purged step renders "the diagram for this change is no longer kept".
    /// </summary>
    public Ulid? StepId { get; init; }

    /// <summary>
    /// What happened to the row this change came from, where it came from an import (IN-8a). An
    /// undo computed from a stored count alone deletes records the import never created.
    /// </summary>
    public string? Disposition { get; init; }

    /// <summary>
    /// For a structural change: how many fields the cap left out, where even their descriptions
    /// would not fit.
    ///
    /// It is a count rather than a silence because the audit record has to be honest about what
    /// it is not saying. A change with this above zero is always
    /// <see cref="Reversibility.NotReversible"/>: there is no inverse here, and the record is of
    /// what happened rather than of how to take it back.
    /// </summary>
    public int OmittedFields { get; init; }

    /// <summary>
    /// What this change costs the journal, in characters of payload (TR-2b). Zero for every
    /// record change, whatever the width of the record: its references are of fixed size.
    /// </summary>
    public int Weight => Fields.Sum(static entry => entry.Weight) + (ContentHash?.Length ?? 0);

    /// <summary>A change to one record - an insert, update or delete - as opposed to a structural one.</summary>
    public bool IsRecordChange => Kind is ChangeKind.Insert or ChangeKind.Update or ChangeKind.Delete;
}

/// <summary>
/// TR-2b, in one place: <b>shaped by the operation first, bounded by size second</b>.
///
/// A record change - <see cref="Insert"/>, <see cref="Update"/>, <see cref="Delete"/> - records
/// the record and two version identifiers and nothing of the values (V-18, AJ-1): the values are
/// in the engine's version history, so the journal of an import of ten thousand records is a
/// fixed cost per record whatever the width of the rows, and no cap decides whether a record
/// change can be undone. A structural change - <see cref="Structural"/> - is where the journal is
/// still the only copy of what went, and there the operation decides what is recorded and the
/// cap decides how much of it survives: large values as a size, a hash and a preview, and a cap
/// per change as well as per value.
///
/// <b>And where the cap cannot hold a structural inverse, the change is declared irreversible
/// rather than truncated.</b> That decision is made here, before the change runs, so that the
/// confirmation card can say so while the person can still decline (AG-11c, AG-11d, D-17).
/// </summary>
public static class DataChanges
{
    /// <summary>
    /// A record was created (TR-2b, V-18): the record and the version its insert produced. An
    /// undo deletes the record by identity, so nothing about its values is needed here, and the
    /// journal of an import does not grow with the width of the rows (AJ-1).
    /// </summary>
    /// <param name="reversibility">
    /// Computed before the change runs from what the collection declares:
    /// <see cref="Reversibilities.OfRecordChange"/> (AJ-5).
    /// </param>
    public static DataChange Insert(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        Ulid versionId,
        Reversibility reversibility)
    {
        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Insert,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            reversibility)
        {
            RecordId = recordId,
            PreviousVersionId = null,
            VersionId = versionId
        };
    }

    /// <summary>
    /// A record was changed (TR-2b, V-18): the version it replaced, which an undo restores, and
    /// the version it produced, which says whether the record has been touched since (AG-11a).
    /// No field values: the before-and-after table is read from the two versions (TR-6).
    /// </summary>
    public static DataChange Update(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        Ulid previousVersionId,
        Ulid versionId,
        Reversibility reversibility)
    {
        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Update,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            reversibility)
        {
            RecordId = recordId,
            PreviousVersionId = previousVersionId,
            VersionId = versionId
        };
    }

    /// <summary>
    /// A record was removed (TR-2b, D-17, V-18): the version it replaced - the record's last
    /// state, which the engine's history keeps and an undo restores under the record's own
    /// identity - and the tombstone the delete produced. The journal is no longer the only copy
    /// of a deleted record, so its payload is as small as any other record change's.
    /// </summary>
    public static DataChange Delete(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        Ulid previousVersionId,
        Ulid tombstoneVersionId,
        Reversibility reversibility)
    {
        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Delete,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            reversibility)
        {
            RecordId = recordId,
            PreviousVersionId = previousVersionId,
            VersionId = tombstoneVersionId
        };
    }

    /// <summary>
    /// A change to the shape of things rather than to a record: a collection, a field, a relation.
    /// The one kind of change whose inverse lives in the journal (AG-11e), shaped and bounded by
    /// TR-2b: each value inside <paramref name="limits"/>, and the whole payload inside the cap.
    ///
    /// The caller states the reversibility because only it knows what it kept, and the cap can
    /// only lower it: a payload the cap could not hold whole makes the change
    /// <see cref="Reversibility.NotReversible"/>, because half an inverse is not one. Removing a
    /// field from a wide collection needs every value that was in it, and whether those fit is a
    /// question about the whole collection rather than about this payload - which is why the
    /// operation is declared irreversible before it runs when they will not (AG-11c, AG-11d).
    /// </summary>
    public static DataChange Structural(
        Ulid requestId,
        ChangeKind kind,
        string collectionName,
        IReadOnlyList<FieldChange> fields,
        Reversibility reversibility,
        PayloadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (kind is ChangeKind.Insert or ChangeKind.Update or ChangeKind.Delete)
        {
            throw new ArgumentException("A record change carries version references, not a payload.", nameof(kind));
        }

        var payload = Bound([.. fields], limits ?? PayloadLimits.Default);

        return new DataChange(
            Next(),
            requestId,
            kind,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            payload.Whole ? reversibility : Reversibility.NotReversible)
        {
            Fields = payload.Fields,
            OmittedFields = payload.Omitted
        };
    }

    /// <summary>
    /// The values a structural change's inverse holds, for putting them back (D-17): field by
    /// field, the value as it was. Empty where a value was too large to keep, which is why such
    /// a change is never offered as reversible in the first place. A record change has nothing
    /// here: its inverse is the version <see cref="DataChange.PreviousVersionId"/> names.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Restore(DataChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var field in change.Fields)
        {
            if (!field.Before.IsWhole || field.Before.IsEmpty) continue;

            fields[field.Name] = field.Before.Value;
        }

        return fields;
    }

    /// <summary>
    /// The cap per change, which is the half of TR-2b that a per-value cap cannot do: a
    /// structural change over five thousand short fields passes every per-value bound there is.
    ///
    /// <b>Three outcomes, and only the first of them keeps an inverse.</b> Inside the cap with
    /// every old value whole, the payload is the inverse and the change is as reversible as the
    /// caller said. Past the cap, <b>the inverse is not kept</b> - every value is given up for its
    /// description, which is what TR-2b means by classifying the operation irreversible instead
    /// of truncating it, and it is why this happens before the change runs rather than after.
    /// Past the cap even then, the descriptions are cut off too and the change says how many
    /// fields it could not name, because a payload that could grow without limit would not be
    /// bounded by anything.
    /// </summary>
    private static (IReadOnlyList<FieldChange> Fields, int Omitted, bool Whole) Bound(
        List<FieldChange> fields,
        PayloadLimits limits)
    {
        // The old side only: the inverse of an update is what the fields were, and a new value
        // too large to keep costs the audit a preview rather than costing the undo anything.
        var whole = fields.All(static field => field.Before.IsWhole);

        var room = limits.LongestPayload;

        // Inside the cap, everything stands as the per-value bound left it. One field that was
        // too large to keep does not cost the other fields their values - only its own record's
        // claim to be reversible.
        if (Weigh(fields) <= room) return (fields, 0, whole);

        var described = fields
            .Select(static field => new FieldChange(field.Name, field.Before.WithoutValue(), field.After.WithoutValue()))
            .ToList();

        if (Weigh(described) <= room) return (described, 0, false);

        var kept = new List<FieldChange>();
        var weight = 0;

        foreach (var field in described)
        {
            weight += field.Weight;

            if (weight > room) break;

            kept.Add(field);
        }

        return (kept, described.Count - kept.Count, false);
    }

    private static int Weigh(IEnumerable<FieldChange> fields) => fields.Sum(static field => field.Weight);

    private static Ulid Next() => Ulid.NewUlid();

    private static string Name(string collectionName) =>
        string.IsNullOrWhiteSpace(collectionName)
            ? throw new ArgumentException("A change has to say which collection it changed.", nameof(collectionName))
            : collectionName;
}
