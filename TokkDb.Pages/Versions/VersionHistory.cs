using TokkDb.Documents;

namespace TokkDb.Pages.Versions;

//RH-1 and V-9. A record's version forest as History returns it: every version in write order,
//each with what its node and its operation say, and the shape — the head, the leaves, and every
//root with where it was cut. Works for a deleted record, whose head is its tombstone.
public sealed class VersionHistory {
  public Ulid RecordId { get; init; }
  public IReadOnlyList<VersionEntry> Versions { get; init; } = [];

  //The most recent version: the live image's, or the tombstone's; null for a record with no
  //history.
  public Ulid? Head { get; init; }
  public IReadOnlyList<Ulid> Leaves { get; init; } = [];
  public IReadOnlyList<Ulid> Roots { get; init; } = [];
  public bool IsDeleted { get; init; }

  //V-8: the operations whose recorded time is earlier than the one before them in write order
  //— the wall clock stepped back between them, and logical time did not.
  public IReadOnlyList<Ulid> OperationsRecordedOutOfOrder { get; init; } = [];

  public bool IsEmpty => Versions.Count == 0;
}

//One version as history shows it: the node, and the operation's attribution beside it.
public sealed class VersionEntry {
  public Ulid VersionId { get; init; }
  public Ulid? Parent { get; init; }
  public VersionKind Kind { get; init; }
  public ushort SchemaVersion { get; init; }
  public int Distance { get; init; }
  public bool IsKeyframe => Distance == 0;
  public bool IsHead { get; init; }
  public bool IsLeaf { get; init; }
  public Ulid? CutFrom { get; init; }
  public Ulid? ReplacedHead { get; init; }
  public DateTimeOffset LogicalTime => VersionId.Time;

  public Ulid OperationId { get; init; }
  public DateTime RecordedAt { get; init; }
  public string Author { get; init; } = string.Empty;
  public Ulid Cause { get; init; }
  public string Comment { get; init; } = string.Empty;
}

//V-10. A value the current schema cannot show, reported rather than dropped: the step that
//could not carry it (by the schema version it produced), the column as it was called before
//that step, the value as the step before left it, and the original as stored. For a delta, the
//element that could not be carried.
public sealed record Unmapped(UnmappedReason Reason, string Column, IDocumentValue Value, IDocumentValue Original,
    ushort SchemaVersion) {
  public Documents.Delta.DeltaElement Element { get; init; }
}

//RH-4. A diff between two versions, mapped through the current schema unless asked for as
//stored, and whether it came from the stored delta of a child against its parent — which
//reads no image — or from two reconstructions.
public sealed class VersionDiff {
  public Ulid From { get; init; }
  public Ulid To { get; init; }
  public Documents.Delta.DocumentDelta Delta { get; init; }
  public IReadOnlyList<Unmapped> Unmapped { get; init; } = [];
  public bool FromStoredDelta { get; init; }
  public long PagesRead { get; init; }
}

public enum UnmappedReason {
  RetypeLossy = 1,
  ColumnRemoved = 2
}

//RH-2. A version read through the current schema.
public sealed class VersionedValue<T> {
  public VersionEntry Version { get; init; }
  public T Value { get; init; }
  public bool IsDeleted { get; init; }
  public IReadOnlyList<Unmapped> Unmapped { get; init; } = [];
}

//RH-2. A version as stored: the document at its own schema version, nothing mapped.
public sealed class StoredVersion {
  public VersionEntry Version { get; init; }
  public ObjectDocument Document { get; init; }
  public ushort SchemaVersion { get; init; }
  public bool IsDeleted { get; init; }
  public ReconstructionReport Report { get; init; }
}

//RB-1, RB-2 and RB-4. What a restore did: the version it wrote as the new head, the head it
//replaced — a tombstone for a deleted record — and every value the current schema could not
//take.
public sealed class RestoreResult {
  public Ulid RecordId { get; init; }
  public Ulid RestoredVersion { get; init; }
  public Ulid NewVersion { get; init; }
  public Ulid ReplacedHead { get; init; }
  public bool WasDeleted { get; init; }
  public IReadOnlyList<Unmapped> Unmapped { get; init; } = [];
}

//RH-3. Why a moment has no version for a record: each a different answer, never a null.
public enum AsOfOutcome {
  Found = 1,
  NotYetCreated = 2,
  Deleted = 3,
  BeforeRecordedHistory = 4,
  NoSuchRecord = 5
}

//RH-3 and RH-8. The record at a moment of logical time, or which of the four reasons applies,
//with the version the floor search landed on and the pages the lookup read.
public sealed class AsOfResult<T> {
  public AsOfOutcome Outcome { get; init; }
  public DateTimeOffset Moment { get; init; }
  public VersionedValue<T> Version { get; init; }
  public Ulid? VersionId { get; init; }
  public long PagesRead { get; init; }
  public bool IsFound => Outcome == AsOfOutcome.Found;
}

//RH-9. The columns and relations of a collection as declared at a moment, or before recorded
//history when no schema node is that old.
public sealed class SchemaSnapshot {
  public DateTimeOffset Moment { get; init; }
  public bool BeforeRecordedHistory { get; init; }
  public SchemaNode Schema { get; init; }
  public IReadOnlyList<Relations.RelationDescriptor> Relations { get; init; } = [];
}

//V-8. Logical time is an identifier's timestamp, to the millisecond a Ulid keeps.
public static class LogicalTime {
  //The greatest identifier whose logical time is at or before the moment: the moment's
  //millisecond, and every bit after it set. The floor of it in a record's range is the record's
  //last version at the moment.
  public static Ulid AtOrBefore(DateTimeOffset moment) {
    var milliseconds = moment.ToUnixTimeMilliseconds();
    if (milliseconds < 0) {
      return Ulid.MinValue;
    }
    var bytes = new byte[16];
    for (var i = 5; i >= 0; i--) {
      bytes[i] = (byte)(milliseconds & 0xFF);
      milliseconds >>= 8;
    }
    for (var i = 6; i < 16; i++) {
      bytes[i] = 0xFF;
    }
    return new Ulid(bytes);
  }
}
