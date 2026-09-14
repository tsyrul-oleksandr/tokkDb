using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Versions;

//HS-5. One version of one record as the history collection holds it.
//
//It carries nothing derivable. The record and the version come from its index key and its
//document header — VersionId is the node document's own record identifier — logical time is
//the version identifier's timestamp (V-8), and attribution belongs to the operation (V-12).
//A keyframe is a node at distance 0 from itself: a root, or a version the count rule or the
//large-delta rule made one (V-1). Its image is kept once it has left the head; while it is
//the head, the live image is the image.
public sealed class VersionNode {
  public Ulid RecordId { get; init; }
  public Ulid VersionId { get; init; }
  public VersionKind Kind { get; init; }

  //Absent for a root: the first node of a record, or a purge root (V-15).
  public Ulid? Parent { get; init; }

  public Ulid OperationId { get; init; }

  //The schema version the delta was computed under.
  public ushort SchemaVersion { get; init; }

  //From the nearest keyframe ancestor; 0 for a keyframe.
  public int Distance { get; init; }

  //Absent for a root, a tombstone and any keyframe (V-1).
  public DocumentDelta Delta { get; init; }

  //For a keyframe that has left the head: its full image, and the schema version it was
  //written under.
  public ObjectDocumentValue Image { get; init; }
  public ushort ImageSchemaVersion { get; init; }

  //For a restore: the head it replaced (V-9).
  public Ulid? ReplacedHead { get; init; }

  //For a purge root: the parent the purge removed (V-15).
  public Ulid? CutFrom { get; init; }

  //Where the node's document lies, as read; what a live image's PreviousVersion addresses
  //(V-6). Not stored in the node: it is the node's own address.
  public DocumentAddress Address { get; init; }

  public bool IsKeyframe => Distance == 0;
  public bool IsRoot => Parent is null;
  public DateTimeOffset LogicalTime => VersionId.Time;

  //WV-2 step 2 and WV-8: the image copied in once the keyframe has left the head. Never
  //replaces an image already held.
  public VersionNode WithImage(ObjectDocumentValue image, ushort imageSchemaVersion) {
    return new VersionNode {
      RecordId = RecordId, VersionId = VersionId, Kind = Kind, Parent = Parent, OperationId = OperationId,
      SchemaVersion = SchemaVersion, Distance = Distance, Delta = Delta, Image = Image ?? image,
      ImageSchemaVersion = Image is null ? imageSchemaVersion : ImageSchemaVersion, ReplacedHead = ReplacedHead,
      CutFrom = CutFrom, Address = Address
    };
  }

  public VersionNode With(DocumentAddress address) {
    return new VersionNode {
      RecordId = RecordId, VersionId = VersionId, Kind = Kind, Parent = Parent, OperationId = OperationId,
      SchemaVersion = SchemaVersion, Distance = Distance, Delta = Delta, Image = Image,
      ImageSchemaVersion = ImageSchemaVersion, ReplacedHead = ReplacedHead, CutFrom = CutFrom, Address = address
    };
  }
}

//HS-6 and V-12. One operation — an outermost transaction that recorded at least one version —
//as recorded once in each history collection it touched. Its identifier orders it in logical
//time; RecordedAt is the wall clock when it recorded its first version, which is what history
//shows people (V-8).
public sealed class Operation {
  public Ulid Id { get; init; }
  public DateTime RecordedAt { get; init; }
  public string Author { get; init; } = string.Empty;

  //The assistant's request identifier today, an event identifier once the event log exists;
  //default when unknown.
  public Ulid Cause { get; init; }
  public string Comment { get; init; } = string.Empty;
  public DateTimeOffset LogicalTime => Id.Time;
}

//V-11 and HS-10. A schema version of a versioned collection as its history records it: the full
//column declarations, uniqueness and element keys included, and the migration steps that
//produced the version — every step SetColumns applied in the one change. Written in the
//transaction of the change, and at switch-on for the current schema and for every step still
//in the catalogue's log, whose earlier declarations are then unknown. Reconstruction builds its
//migrator from these, never from the catalogue, which Rewrite empties.
public sealed class SchemaNode {
  //Minted by the store when the node is recorded; its timestamp is the node's logical time.
  public Ulid Id { get; init; }
  public ushort SchemaVersion { get; init; }
  public IReadOnlyList<ColumnDescriptor> Columns { get; init; } = [];

  //False for a version recorded at switch-on from a migration step alone: the step is known,
  //the columns it produced are not.
  public bool ColumnsKnown { get; init; } = true;
  public IReadOnlyList<ColumnMigration> Migrations { get; init; } = [];

  //Recorded at switch-on: what the collection looked like before this version was not
  //recorded, because nothing was recording then.
  public bool UnknownEarlierDeclarations { get; init; }
  public DateTimeOffset LogicalTime => Id.Time;

  public static SchemaNode Of(CollectionDescriptor descriptor, IEnumerable<ColumnMigration> migrations) {
    ArgumentNullException.ThrowIfNull(descriptor);
    return new SchemaNode {
      SchemaVersion = descriptor.SchemaVersion,
      Columns = descriptor.Columns.ToList(),
      ColumnsKnown = true,
      Migrations = migrations?.ToList() ?? []
    };
  }

  public SchemaNode WithId(Ulid id) {
    return new SchemaNode {
      Id = id, SchemaVersion = SchemaVersion, Columns = Columns, ColumnsKnown = ColumnsKnown,
      Migrations = Migrations, UnknownEarlierDeclarations = UnknownEarlierDeclarations
    };
  }
}

public enum RelationNodeKind {
  Created = 1,
  Removed = 2
}

//V-11 and HS-10. The creation or removal of a relation naming a versioned collection, with the
//relation's declaration as it was. A related restore follows relations as they were declared
//at the moment (V-16), which is what these are for.
public sealed class RelationNode {
  public Ulid Id { get; init; }
  public RelationNodeKind Kind { get; init; }
  public Relations.RelationDescriptor Relation { get; init; }

  //Recorded at switch-on for a relation that already existed: created at some unrecorded time.
  public bool UnknownCreationTime { get; init; }
  public DateTimeOffset LogicalTime => Id.Time;

  public static RelationNode Created(Relations.RelationDescriptor relation, bool unknownCreationTime = false) {
    ArgumentNullException.ThrowIfNull(relation);
    return new RelationNode { Kind = RelationNodeKind.Created, Relation = relation, UnknownCreationTime = unknownCreationTime };
  }

  public static RelationNode Removed(Relations.RelationDescriptor relation) {
    ArgumentNullException.ThrowIfNull(relation);
    return new RelationNode { Kind = RelationNodeKind.Removed, Relation = relation };
  }

  public RelationNode WithId(Ulid id) {
    return new RelationNode { Id = id, Kind = Kind, Relation = Relation, UnknownCreationTime = UnknownCreationTime };
  }
}

//RH-5 and RH-8. A version reconstructed as stored: the document at the version's own schema
//version — the live image for the head, and for any other version the nearest image above it
//migrated and re-applied node by node (V-3) — with the report of what that took. For a
//tombstone there is no document, and IsDeleted says so.
public sealed class Reconstruction {
  public VersionNode Node { get; init; }
  public ObjectDocument Document { get; init; }
  public ushort SchemaVersion { get; init; }
  public bool IsDeleted { get; init; }
  public ReconstructionReport Report { get; init; }
}

//RH-8: what a history read cost, for the bounds of WV-5 and RH-3 to be asserted on rather
//than on timings.
public sealed class ReconstructionReport {
  public int VersionsExamined { get; init; }
  public Ulid Keyframe { get; init; }
  public int DeltasApplied { get; init; }
  public long PagesRead { get; init; }
  public TimeSpan Elapsed { get; init; }

  public override string ToString() {
    return $"{VersionsExamined} versions examined from keyframe {Keyframe}, {DeltasApplied} deltas applied, " +
      $"{PagesRead} pages read, {Elapsed.TotalMilliseconds:F1} ms";
  }
}

//RP-7 and VR-10. What a history holds, counted by one scan: the nodes and how many are
//keyframes, the operations, the bytes of deltas and of images as stored, the pages the history
//collection and its version index occupy, and the oldest schema version any node still
//refers to — the one Rewrite cannot let the schema history forget.
public sealed class HistoryReport {
  public string CollectionName { get; init; }
  public int Records { get; init; }
  public int Nodes { get; init; }
  public int Keyframes { get; init; }
  public int Operations { get; init; }
  public long DeltaBytes { get; init; }
  public long ImageBytes { get; init; }
  public int DataPages { get; init; }
  public int IndexPages { get; init; }
  public int Pages => DataPages + IndexPages;
  public ushort? OldestSchemaVersion { get; init; }

  public override string ToString() {
    return $"{CollectionName}: {Records} records, {Nodes} nodes ({Keyframes} keyframes), {Operations} operations, " +
      $"{DeltaBytes} delta bytes, {ImageBytes} image bytes, {Pages} pages ({DataPages} data, {IndexPages} index), " +
      $"oldest schema version {(OldestSchemaVersion is { } oldest ? oldest.ToString() : "none")}";
  }
}

//WV-10 and RP-8. What Verify found: every place the history breaks an invariant, each named
//by the node or record it concerns, and what was looked at. Sound when nothing is listed.
public sealed class HistoryVerification {
  public IReadOnlyList<string> Problems { get; init; } = [];
  public int Records { get; init; }
  public int Nodes { get; init; }
  public int IndexEntries { get; init; }
  public bool IsSound => Problems.Count == 0;

  //RP-8: the node the first problem is at, when the problem is a node's.
  public Ulid? FailingRecord { get; init; }
  public Ulid? FailingVersion { get; init; }
  public string FirstProblem => Problems.Count == 0 ? null : Problems[0];

  public override string ToString() {
    return IsSound
      ? $"sound: {Records} records, {Nodes} nodes, {IndexEntries} index entries"
      : $"{Problems.Count} problems: " + string.Join("; ", Problems);
  }
}
