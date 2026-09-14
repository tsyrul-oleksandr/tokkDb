using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Versions;

//§3.1. The whole of layer 1's contract. Layer 1 records what the write seam tells it,
//reconstructs what it is asked for, and verifies itself; it knows nothing about restore,
//purge, erasure, relations or the assistant. Layers 2 to 4 use it only through here (HS-3).
//
//Every operation runs inside the caller's transaction. The signatures of the operations a
//later step implements are refined by that step, which updates §3.1 when it does.
public interface IVersionStore {
  //Lifecycle (V-4, V-14): the connection at open, SetRetentionPolicy and DropCollection only.
  //Initialize reads the schema and relation history of every history collection into memory,
  //at open and after a catalogue reload, so that nothing ever scans for them (HS-4).
  //CreateHistory makes the collection's history collection, links it through
  //HistoryCollectionId and records the schema and relations as they stand (HS-10), in the
  //caller's transaction; DropHistory retires every document in it, drops its version index and
  //removes it from the catalogue, so that secure release reaches all of it (HS-2).
  void Initialize();
  CollectionDescriptor CreateHistory(string collectionName);
  void DropHistory(string collectionName);

  //Recording (V-7): the write seam and schema changes only, inside the write's transaction.
  //RecordInsert writes the Insert node of a new record's first image and returns the address
  //the image's PreviousVersion takes (WV-1). RecordSupersede takes the head as stored — its
  //header and its unmigrated image, for the keyframe copy and a Baseline (WV-4) — the new
  //image's header, whose VersionId the seam has minted after the head's (HS-7), the new image,
  //the delta the seam computed from the head as the current schema reads it, and, for a
  //restore, the version restored, which becomes the parent (V-9); it copies the head's image
  //into its keyframe node when that is due, writes the node for the change (WV-2 steps 2 and 4)
  //and returns its address. A schema or relation node is recorded for a collection that keeps
  //versions and ignored for one that does not; the node comes back with its identifier, or null
  //when nothing was recorded. RecordDelete takes the head as stored, gives its node the image
  //if that node is a keyframe — or writes its Baseline first (WV-4) — and adds the Delete
  //tombstone whose parent is the head (WV-3); it returns the tombstone's address, which no
  //live image points at.
  DocumentAddress RecordInsert(string collectionName, RecordHeader head);
  DocumentAddress RecordSupersede(string collectionName, VersionKind kind, RecordHeader previousHead,
      ObjectDocument previousImage, RecordHeader newHead, ObjectDocument newImage, DocumentDelta delta,
      Ulid? restoredVersion);
  DocumentAddress RecordDelete(string collectionName, RecordHeader head, ObjectDocument image);
  //WV-8: for Rewrite only. Stores the head's image in its node before Rewrite migrates it —
  //writing the head's Baseline when it has none — and returns the node's address, which is
  //where a zero pointer moves to. A node's image, once stored, is never replaced.
  DocumentAddress PreserveImage(string collectionName, RecordHeader head, ObjectDocument storedImage);
  SchemaNode RecordSchema(string collectionName, SchemaNode schema);
  RelationNode RecordRelation(string collectionName, RelationNode relation);

  //Reading (layers 2 to 4). Schema and relation nodes come from memory (V-5); SchemaAt gives
  //the migration steps between two schema versions from schema history, never from the
  //catalogue. Refined at step 4.1.
  VersionNode Head(string collectionName, Ulid recordId);
  VersionNode Node(string collectionName, Ulid recordId, Ulid versionId);
  IReadOnlyList<VersionNode> Nodes(string collectionName, Ulid recordId);
  VersionNode Floor(string collectionName, Ulid recordId, Ulid atOrBefore);
  Operation Operation(string collectionName, Ulid operationId);
  IReadOnlyList<SchemaNode> SchemaNodes(string collectionName);
  IReadOnlyList<RelationNode> RelationNodes(string collectionName);
  IReadOnlyList<ColumnMigration> SchemaAt(string collectionName, ushort fromVersion, ushort toVersion);
  Reconstruction Reconstruct(string collectionName, Ulid recordId, Ulid versionId);

  //Maintenance (layer 4 only). Refined at steps 7.1 and 7.3.
  void Reroot(string collectionName, Ulid recordId, Ulid versionId, ObjectDocument image, Ulid cutFrom);
  void Remove(string collectionName, Ulid recordId, IReadOnlyCollection<Ulid> versionIds);
  void EraseRecord(string collectionName, Ulid recordId);

  //Verification (layer 4 and tests): WV-10's four invariants over the whole history, by scans.
  HistoryVerification Verify(string collectionName);
}
