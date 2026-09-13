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
/// preview. <see cref="DataChanges"/> is that rule, written once so that no caller can shape a
/// payload its own way.
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
    /// What changed, field by field, old beside new (TR-2a).
    ///
    /// Shaped by <see cref="Kind"/> and not by what was convenient: empty for an insert, whose
    /// data is in storage and whose identity and <see cref="ContentHash"/> are the whole record
    /// of it; the changed fields for an update; every field for a delete.
    /// </summary>
    public IReadOnlyList<FieldChange> Fields { get; init; } = [];

    /// <summary>
    /// A hash of the record as it was written, so that "has this been touched since" stays
    /// answerable for the commonest case, which is an import (AG-11a).
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
    /// How many fields the cap left out, where even their descriptions would not fit.
    ///
    /// It is a count rather than a silence because the audit record has to be honest about what
    /// it is not saying. A change with this above zero is always
    /// <see cref="Reversibility.NotReversible"/>: there is no inverse here, and the record is of
    /// what happened rather than of how to take it back.
    /// </summary>
    public int OmittedFields { get; init; }

    /// <summary>What this change costs the journal, in characters of payload (TR-2b).</summary>
    public int Weight => Fields.Sum(static entry => entry.Weight) + (ContentHash?.Length ?? 0);
}

/// <summary>
/// TR-2b, in one place: <b>shaped by the operation first, bounded by size second</b>.
///
/// The order is the requirement and it is not a preference. A purely size-based rule would
/// truncate the one payload that cannot be truncated - a delete, where the journal is about to be
/// the only copy of the record - while happily keeping a full copy of an insert, whose data is
/// still in storage and whose payload is therefore the one that can afford to be almost nothing.
/// So the operation decides what is recorded, and the cap then decides how much of it survives.
///
/// <b>And where the cap cannot hold the inverse, the change is declared irreversible rather than
/// truncated.</b> That decision is made here, before the change runs, so that the confirmation
/// card can say so while the person can still decline (AG-11c, AG-11d, D-17).
/// </summary>
public static class DataChanges
{
    /// <summary>
    /// A record was created (TR-2b).
    ///
    /// <b>The identity and a content hash, and nothing else.</b> This is what makes the journal
    /// of an import of ten thousand records a fixed cost per record rather than a second copy of
    /// the file: the payload of an insert does not grow with the width of the row, because the
    /// row is in storage and the only question the journal has to keep answering is whether it
    /// has been touched since (AG-11a). Undoing an insert needs the identity, which is here.
    /// </summary>
    public static DataChange Insert(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        IReadOnlyDictionary<string, object?> written)
    {
        ArgumentNullException.ThrowIfNull(written);

        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Insert,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            Reversibility.Reversible)
        {
            RecordId = recordId,
            ContentHash = Hashes.OfFields(written)
        };
    }

    /// <summary>
    /// A record was changed: the fields that differ, before and after (TR-2a, TR-2b).
    ///
    /// Fields that did not change are not recorded. An update that touches one field of a record
    /// of eighty is a payload of one field, and the undo of it is the same size - which is the
    /// point of recording the change rather than the record.
    /// </summary>
    public static DataChange Update(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after,
        PayloadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var bounds = limits ?? PayloadLimits.Default;
        var fields = new List<FieldChange>();

        foreach (var name in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal)
                     .OrderBy(static name => name, StringComparer.Ordinal))
        {
            var was = before.GetValueOrDefault(name);
            var now = after.GetValueOrDefault(name);

            if (Same(was, now)) continue;

            fields.Add(new FieldChange(name, JournalValue.Of(was, bounds), JournalValue.Of(now, bounds)));
        }

        var payload = Bound(fields, bounds);

        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Update,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            // An update is unconditionally reversible when its inverse is whole: putting the old
            // values back cannot violate a rule the new ones satisfy. When a before value was too
            // large to keep, there is no inverse to put back and saying otherwise would be a lie.
            payload.Whole ? Reversibility.Reversible : Reversibility.NotReversible)
        {
            RecordId = recordId,
            Fields = payload.Fields,
            OmittedFields = payload.Omitted,
            ContentHash = Hashes.OfFields(after)
        };
    }

    /// <summary>
    /// A record was removed: <b>the whole record</b> (TR-2b, D-17).
    ///
    /// The one payload that is not allowed to be economical. Everywhere else the journal can
    /// record less because storage still has the data; here it is about to have nothing, and
    /// what is written down is the only thing standing between a deleted record and its being
    /// gone. If that cannot be written down whole, the delete is
    /// <see cref="Reversibility.NotReversible"/> and the person is told before it runs.
    ///
    /// Whole and restorable are still not the same thing, which is why a delete is
    /// <see cref="Reversibility.ReversibleWithConditions"/> at best: a uniqueness rule or a
    /// relation created since the deletion can refuse the record on its way back in, and that is
    /// found out when the undo is attempted rather than when it is offered.
    /// </summary>
    public static DataChange Delete(
        Ulid requestId,
        string collectionName,
        Ulid recordId,
        IReadOnlyDictionary<string, object?> removed,
        PayloadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(removed);

        var bounds = limits ?? PayloadLimits.Default;

        var fields = removed.Keys
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name => FieldChange.Removed(name, JournalValue.Of(removed[name], bounds)))
            .ToList();

        var payload = Bound(fields, bounds);

        return new DataChange(
            Next(),
            requestId,
            ChangeKind.Delete,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            payload.Whole ? Reversibility.ReversibleWithConditions : Reversibility.NotReversible)
        {
            RecordId = recordId,
            Fields = payload.Fields,
            OmittedFields = payload.Omitted,
            ContentHash = Hashes.OfFields(removed)
        };
    }

    /// <summary>
    /// A change to the shape of things rather than to a record: a collection, a field, a relation.
    ///
    /// The caller states the reversibility because only it knows what it kept. Removing a field
    /// needs every value that was in it, and whether those fit is a question about the whole
    /// collection rather than about this payload - which is why AG-11e puts the inverse in the
    /// journal and TR-2b lets the operation be declared irreversible when it will not fit.
    /// </summary>
    public static DataChange Structural(
        Ulid requestId,
        ChangeKind kind,
        string collectionName,
        IReadOnlyList<FieldChange> fields,
        Reversibility reversibility)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return new DataChange(
            Next(),
            requestId,
            kind,
            Name(collectionName),
            DateTimeOffset.UtcNow,
            reversibility)
        {
            Fields = fields
        };
    }

    /// <summary>
    /// The record as it was, for putting it back (D-17). Empty where a value was too large to
    /// keep, which is why such a change is never offered as reversible in the first place.
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
    /// The cap per change, which is the half of TR-2b that a per-value cap cannot do: a record of
    /// five thousand short fields passes every per-value bound there is.
    ///
    /// <b>Three outcomes, and only the first of them keeps an inverse.</b> Inside the cap with
    /// every old value whole, the payload is the inverse and the change is as reversible as its
    /// kind allows. Past the cap, <b>the inverse is not kept</b> - every value is given up for its
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

        // The cap is over the change, so the fixed-width part of it - the content hash every
        // update and delete carries - comes out of the room the fields have.
        var room = limits.LongestPayload - Hashes.Length;

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

    /// <summary>
    /// Whether two values are the same value, so that an update records what changed rather than
    /// what was written. Compared as the journal will keep them, which settles the awkward pairs
    /// - a <see cref="long"/> beside an <see cref="int"/>, two moments in different offsets -
    /// the same way the hash does.
    /// </summary>
    private static bool Same(object? was, object? now)
    {
        if (was is null && now is null) return true;
        if (was is null || now is null) return false;

        return JournalValue.KindOf(was) == JournalValue.KindOf(now)
               && string.Equals(JournalValue.TextOf(was), JournalValue.TextOf(now), StringComparison.Ordinal);
    }

    private static Ulid Next() => Ulid.NewUlid();

    private static string Name(string collectionName) =>
        string.IsNullOrWhiteSpace(collectionName)
            ? throw new ArgumentException("A change has to say which collection it changed.", nameof(collectionName))
            : collectionName;
}
