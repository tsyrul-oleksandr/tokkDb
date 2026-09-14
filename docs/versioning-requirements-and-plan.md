# Versioning — requirements and development plan

**Status.** Draft 6, written 2026-09-13 against the working tree of that date. That tree includes the
trace model and recorder (`8e18bfb`) and the entity-query plan's Phase 1, still in progress
(`CatalogLock`, `ChangeSchema`, `DbQuery`). The appendices list what changed in drafts 2 to 6.

**What this is.** The plan that finishes Chapter 2.4 in the engine. Version history is produced by
the DBMS itself and stored as deltas with periodic keyframes. A record can be read as of a version
or a moment, two versions can be diffed, and a version can be restored as a new branch. History is
removed only by an explicit purge or erase. This plan settles the question D-5 of
`requirements-and-plan.md` left open, and replaces that plan's Phase 8.

**What this is not.** It is not a version view in any interface (UI-1 stays open). It is not a
general history API for applications. It is not the event log of Chapter 2.1: nodes leave a field
for event references, and no log is built. And it is not time travel over whole queries: history is
read one record at a time.

**The constraint everything below answers to: history preserves the Delta.** Each version stores
what changed — path, operation, old value, new value. A full image of a record is kept only where
the bound on reconstruction cost needs one, or where the delta would be as large as the image. The
full-copy approach §2.4 criticises survives only as the benchmark baseline (V-1).

---

## 1. Scope

### 1.1 In scope

| | |
|---|---|
| The delta | Path-level differences between two documents, nested objects and arrays included, that can be inverted and replayed exactly |
| The version store | One history collection per versioned collection: version nodes, keyframe images, operations, schema history, and a B+Tree over `(recordId, versionId)` with a floor search |
| Recording | Insert, update, delete and restore each produce one version inside the write's transaction; every version belongs to the operation (unit of work) that wrote it |
| Reading | A record's version tree; the record as of a version or a moment; the difference between two versions, with anything the current schema cannot show reported |
| Restoring | A version restored as a new branch; a deleted record restored; point-in-time restore along outgoing relations |
| Retention and erasure | A persisted policy per collection; purge before a moment, per collection or per record; erasure within a stated boundary |
| The assistant | Record changes in the journal become version references, and undo becomes restore |
| Measurement | The keyframe interval, the large-delta rule and the write cost measured before versioning becomes the default |

### 1.2 Out of scope

Each of these is recorded in the decision register (§4) as a future enhancement, with what it would
need.

- A version view in `TokkDb.LLM.Application` (UI-1).
- History reads (`History`, `GetAsOf`) on either application's storage contract, beyond what undo
  needs.
- Versioning the reserved collections, the catalogue included.
- Queries over a whole collection as of a moment.
- Merging branches.
- Turning versioning off while keeping history with a recorded gap.
- Following incoming relations in a related restore, and finding a relation's holder by searching
  history.
- Erasure outside the boundary of V-17: process memory, the operating system, the file system,
  backups.
- Undoing a structural change (a removed column, a dropped collection) from history.
- A cumulative-bytes keyframe rule beside the count rule and the large-delta ratio.

---

## 2. What is already in place, and what it does not yet say

Phase 3 of the engine plan wrote four pieces of insurance (D-5) so that versioning would be a change
of behaviour rather than a change of format. All four are there. Read against a delta design, they
also turn up thirteen places where the code does not yet say what versioning needs.

### 2.1 In place

| Artefact | Where | What it gives this plan |
|---|---|---|
| Record header (VR-11) | `TokkDb.Pages/Records/RecordHeader.cs` | `RecordId`, `VersionId`, `PreviousVersion (pageId, slotId)`, `Flags` and `SchemaVersion` on every image written since Phase 3. No record has to be rewritten to gain a version |
| Record flags | `TokkDb.Pages/Records/RecordFlags.cs` | `Live`, `Superseded`, `Deleted`, `HasOverflow` |
| Policy and reason | `TokkDb.Pages/Records/RetentionPolicy.cs` | `None`, the reserved `KeepVersions`, and `RemoveReason`, which the version store branches on |
| The seam (VR-12) | `DbEntities.RemoveCurrentVersion` | The one place an update or delete of a user record retires its image. Today it throws for `KeepVersions` |
| Copy-on-write update | `DbEntities.Update` | Retire, then write, in one transaction. A crash leaves the old image readable (`MutableRecordTests`) |
| Retirement | `DataPageManager.RetireRow` | Marks the header before freeing the slot, frees the overflow chain, removes index entries |
| Catalogue fields | `CollectionDescriptor.HistoryCollectionId`, `.RetentionPolicy` | Persisted with defaults and read by nothing — reserved for the link V-4 makes |
| Format version (ST-9) | `TokkDb.Pages/RootPage.cs` | Stays at 2: nothing in this plan changes a page layout (NF-7) |
| Documents in the catalogue (D-4) | `CollectionDescriptorDocument`, `SystemDocumentStore` | A new catalogue field is a new document field; a version node is a document |
| B+Tree and roots | `BPlusTree`, `IndexRoot` | The version index is another tree with another root (V-5) |
| Composite key (D-3) | `TokkDb.Documents/Keys/CompositeKey.cs` | `CompositeKey.Encode(recordId, versionId)` keeps a record's versions together, in creation order |
| Monotonic identity | `TokkDb.Pages/Records/RecordIdentity.cs` | Ulids ordered within a millisecond, which version order relies on (V-8) |
| Lazy migration (DC-7) | `SchemaMigrator`, `ColumnMigration` | Brings an image written under an older schema up to a newer one — what reconstruction does between a keyframe and a delta (V-3) |
| Overflow chains (ST-5) | `DataPageManager.WriteOverflowRecord` | A keyframe or a large delta is not capped at a page |
| Transactions and fault injection (TX-1…TX-3) | `Transaction`, `Journal`, `FaultInjectingDiskManager` | Node, index entry, operation and live image commit together, and the harness to prove it |
| Catalogue lease (QM-2b) | `CatalogLock`, `TokkDbConnection.ChangeSchema` | A history read holds a lease; a policy change is a schema change |
| The assistant's journal (TR-2b, D-17) | `TokkDb.Assistant.Trace/DataChange.cs`, `ChangePayload.cs` | The record of which request changed which record, which V-18 reduces to version references |
| File-size baseline | `TokkDb.Benchmarks/Benchmarks/FileSizeGrowthBenchmark.cs` | Written as "the baseline for NFR-4 later" |

### 2.2 What it does not yet say

1. **VR-12 describes the full-copy model.** It says that under `KeepVersions` "the superseded image
   is retained and linked through `previousVersion`", which keeps every image of every record — the
   approach Fig. 2.6 criticises. V-1 and V-6 change what is retained and what the pointer addresses.
   The layout stays as it is.
2. **Version identifiers are not monotonic.** `RecordHeader.ForNewRecord` mints `VersionId` with
   `Ulid.NewUlid()`, which D-1's own qualification measured as random within a millisecond (HS-7).
3. **The policy is supplied in three places, and the one that is persisted is never read.**
   `DbEntities.RetentionPolicy` is a settable property of one handle, `RetireRow` takes a
   `RetentionPolicy` argument on every call, and `CollectionDescriptor.RetentionPolicy` is persisted and
   never read (HS-1).
4. **`RetireRow` has seven callers, not one.** Its comment says it is called only from the
   `RemoveCurrentVersion` seam. It is also called by `SystemDocumentStore.Write` and `.Delete`, by the
   three catalogues, and by `DropCollection`. The first five touch only reserved collections.
   `DropCollection` does retire every record of a user collection — versioned ones included — so it is a
   second path to versioned content. It is legitimate because it removes the history with the records,
   but it has to be named and tested, not assumed away (HS-2, HS-3).
5. **Four call sites rewrite an image in place.** `SystemDocumentStore.Write`,
   `DataPageManager.MigrateRow`, and `CollectionCatalog.Append` and `.Save` call `UpdateRow` when the
   new bytes fit; the last two touch only `_collections`. None changes a versioned record's content
   (WV-8).
6. **`Rewrite` drops the migration log.** A version written under an older schema could not be
   brought up to date from the catalogue afterwards. V-11 keeps schema history in the history
   collection, so `Rewrite` can go on dropping the log.
7. **Reserved collections are not indexed.** `DataPageManager.IsIndexed` is false for any `_` name,
   and `IndexCatalog.Create` refuses one, so `FindLiveRow` on a history collection is a scan of every
   document in it. The version store owns its tree, and finds every document it needs through that
   tree or from memory, never by that scan (V-5).
8. **Freeing space does not clear it.** `BaseItemsPage.FreeItem` marks a slot free and leaves its
   bytes. `Compact` leaves a copy of every record it moves beyond the new end of the run, and a freed
   overflow page keeps its payload. An in-place rewrite (`UpdateRow`) writes the new bytes over the old
   and leaves whatever lay beyond them in the slot. Dropping an index (`IndexCatalog.Drop`, which
   `DropCollection` and `SetColumns` both use) only records its node pages as retired, so every key —
   values and truncated prefixes — stays in the file. `DropCollection` removes the collection's primary
   entries one by one through the tree and clears none of them. Right for space, wrong for erasure
   (V-17).
9. **Transaction identifiers do not survive a connection.** `TransactionManager` numbers transactions
   with a `ulong` counter that starts again on every open, so it cannot name an operation in history.
   An operation needs an identifier of its own (V-7).
10. **The B+Tree has no floor search.** `BPlusTree` offers an exact `Find` and a forward `Range`.
    "The last version at or before a moment" would have to walk every earlier version of the record
    (V-5).
11. **A committed journal frame outlives its commit.** `PageManager.CommitPages` writes the before
    images, writes the pages, and appends a commit record. Nothing discards the frame until the next
    transaction begins or the next open recovers. After a transaction that erased something, the
    journal file therefore still holds the erased values as before images — even after the
    connection closes (V-17).
12. **The monotonic source does not survive a restart.** `RecordIdentity` keeps its last identifier in
    a static field, which starts empty in every process. After a restart with a clock set behind the
    last write, `Next()` returns identifiers smaller than ones already stored: a new version would
    sort before its record's head, and a new schema node, relation node or operation before its
    predecessor (V-8).
13. **Comments and documents describe the old behaviour.** Each of these is false today or becomes false at a known step,
    and is corrected in that step, not later (§10):

    | Comment or document | Corrected in step |
    |---|---|
    | `RecordFlags`: only `Live` is written | 2.1 |
    | `RecordHeader`: only `RecordId`, `Flags` and `SchemaVersion` are read, and `VersionId` is a fresh random Ulid | 2.1 |
    | `CollectionDescriptor`: `RetentionPolicy` is never read | 2.2 |
    | `DataPageManager.RetireRow`: its "exactly one place" claim, and the policy it refuses | 2.2 |
    | `CollectionDescriptor`: `HistoryCollectionId` is never read | 2.3 |
    | `RecordHeader`: `PreviousVersion` is zero throughout this pass | 3.2 |
    | `RetentionPolicy.KeepVersions`: reserved and refused | 3.2 |
    | `DbEntities.RetentionPolicy` and `RemoveCurrentVersion`: `KeepVersions` is not implemented | 3.2 |
    | `docs/requirements-and-plan.md`: the audit row "Versioning — none", gap row 3, the D-5 risk | 8.3 |

    The table is a starting list, not the whole duty. Any step that changes behaviour corrects every
    comment and document describing the old behaviour, and says which ones.

---

## 3. Layers and contracts

Versioning touches schema migration, restore, retention, erasure, relations, attribution and the
assistant. None of those may reach into the others' internals. The core that records and
reconstructs versions is kept minimal, and everything else is a layer above it that uses a stated
contract.

```
 Layer 5  Assistant integration        TokkDb.Assistant.*        outside the engine
          ──────────────────────────────────────────────────────────────────────────
 Layer 4  Retention and erasure        purge, erase, disabling   ┐ use IVersionStore and
 Layer 3  Restore                      branches, deleted, related┘ the ordinary write path
          ──────────────────────────────────────────────────────────────────────────
 Layer 2  History reads                History, GetAsOf, Diff    read-only over IVersionStore
          ──────────────────────────────────────────────────────────────────────────
 Layer 1  Version store core           IVersionStore             records, reconstructs, verifies
          ──────────────────────────────────────────────────────────────────────────
 Layer 0  Delta library                TokkDb.Documents.Delta    pure, no storage

 Beside   Secure release               page layer                clears released bytes; knows
                                                                 nothing about versions
```

**The rules.**

1. Layer 0 depends on the document model only.
2. Layer 1 knows nothing about restore, purge, erasure, relations or the assistant. It records what
   the write seam tells it, reconstructs what it is asked for, and verifies itself.
3. Only the write seam in `DbEntities` calls layer 1's recording operations, and only inside the
   write's transaction.
4. Layers 2, 3 and 4 use layer 1 only through `IVersionStore`. No type outside
   `TokkDb.Pages.Versions` reads a node document or the version index. An architecture test asserts
   it (HS-3).
5. A restore is an ordinary write through the seam, so it is recorded like any other write — with the
   delta base V-7 states for it — and uniqueness and relations are checked like any other write.
6. Secure release is a property of the page layer. Erasure relies on it; versioning does not own it.
7. Layer 5 uses only the engine's public surface (§3.2) and the assistant's own storage contract.

### 3.1 `IVersionStore`

The whole of layer 1's contract. A step may refine a signature, and updates this list when it does.
Step 2.6 added `Initialize`, which reads the schema and relation nodes of every history collection into
memory at open and after a catalogue reload, and the two readers of what it loaded; `SchemaAt` takes two
schema versions and returns the migration steps between them. Step 3.4 added `PreserveImage`, the one
recording operation `Rewrite` uses (WV-8). `RecordSupersede` takes the delta the seam computed, so that
the seam decides whether anything changed (WV-2 step 1) and the store decides where it is kept.
Step 7.1 refined the maintenance row: `Reroot` takes the image's schema version beside the image and the
parent it records as `cutFrom`; `NextRecords` walks the records with nodes in batches, which is how the
collection-wide purge takes them (I-7); `RemoveUnreferencedOperations` is the pass that ends it. Step 7.3
made `EraseRecord` retire every node of a record with its index entry and mark the transaction for V-17's
journal rule; `DropHistory` marks it too. Step 7.4 added `Report`, the one scan behind `HistoryReport`, and
made `Verify` name the first failing node, which `VerifyHistory` builds on.

| Group | Operation | Used by |
|---|---|---|
| Lifecycle | `Initialize()`, `CreateHistory(collection)`, `DropHistory(collection)` | the connection at open, `SetRetentionPolicy` and `DropCollection` only |
| Recording | `RecordInsert`, `RecordSupersede` (update or restore), `RecordDelete`, `RecordSchema`, `RecordRelation`, `PreserveImage` | the write seam and schema changes only; `PreserveImage` by `Rewrite` only (WV-8) |
| Reading | `Head(record)`, `Node(record, version)`, `Nodes(record)`, `Floor(record, moment)`, `Operation(id)`, `SchemaNodes(collection)`, `RelationNodes(collection)`, `SchemaAt(from, to)`, `Reconstruct(record, version)` | layers 2–4 |
| Maintenance | `Reroot(record, version, image, imageSchemaVersion, cutFrom)`, `Remove(record, versions)`, `NextRecords(after, limit)`, `RemoveUnreferencedOperations()`, `EraseRecord(record)` | layer 4 only |
| Verification | `Verify(collection)`, `Report(collection)` | layer 4, tests |

### 3.2 The public surface this plan adds

| Where | Member | Step |
|---|---|---|
| `TokkDb.Documents.Delta` | `DeltaPath`, `DeltaOperation`, `DeltaElement`, `DocumentDelta`, `DocumentDiff.Compute(a, b, options)`, `DocumentDelta.ApplyTo(value)`, `DocumentDelta.Invert()`, `CanonicalValue.Equal(a, b)`, `DeltaMismatchException` | 1.1–1.5 |
| `TokkDb.Pages.Versions` | `IVersionStore`, `VersionStore`, `VersionNode`, `VersionKind`, `Operation`, `SchemaNode`, `RelationNode`, `VersionIndexRoot`, `VersionAttribution`, `Unmapped`, `Reconstruction`, `ReconstructionReport`, `HistoryReport`, `HistoryVerification`, `VersionHistory`, `VersionEntry`, `VersionedValue<T>`, `StoredVersion`, `AsOfResult<T>`, `AsOfOutcome`, `SchemaSnapshot`, `SchemaMapping`, `VersionDiff`, `LogicalTime`, `HistoryCollections`, `HistoryDocuments`, `VersionNotFoundException`, `RestoreResult`, `RestoreRefusedException`, `RestoreRefusal`, `RelatedRestoreResult`, `RestoredRecord`, `RelatedRestoreRefusedException`, `RelatedRestoreRefusal` | 2.3–6.3 |
| `BPlusTree` | `Floor(key)` | 2.4 |
| `TokkDbConnection` | `SetRetentionPolicy(collection, policy, snapshotInterval, largeDeltaRatio, dropHistory)`, `Attribute(VersionAttribution)`, `PurgeHistory(collection, before, batchSize)` and its `PurgeReport`, `SchemaAsOf(collection, moment)`, `HistoryReport(collection)`, `VerifyHistory(collection)` | 2.2, 2.3, 2.5, 4.3, 7.1, 7.4 |
| `DbEntities<T>` | `RetentionPolicy { get; }`, `HeadVersion(recordId)`, `History(recordId)`, `GetAsOf(recordId, versionId)`, `GetAsOf(recordId, moment)`, `GetStoredAsOf(recordId, versionId)`, `Diff(recordId, from, to)`, `Restore(recordId, versionId)`, `RestoreAsOf(recordId, moment, followRelations, cap)`, `PurgeHistory(recordId, before)`, `Erase(recordId)` | 3.2–7.3 |
| `TokkDb.Assistant.Trace` | `DataChange.PreviousVersionId`, `DataChange.VersionId` | 9.1 |
| `TokkDb.Assistant.Storage` | `IStorage.HeadVersion`, `.Keeps`, `.DiffVersions`, `.RestoreVersion`, `.PurgeRecordHistory`, `.Erase` | 9.2 |

`Insert`, `Update` and `Delete` keep their signatures. A caller that needs the version a write
produced reads `HeadVersion` inside the same unit of work.

---
## 4. Decisions

Every decision has one of three classes.

- **Blocking** — it changes what is stored or what a guarantee means. Settled in this document.
  Phase 3 does not start while any blocking decision is open.
- **Implementation-time** — a value or technique that changes neither stored shapes nor guarantees.
  Settled at the step named, from measurement where there is one.
- **Future** — not in this plan. Recorded with what it would need.

### 4.1 Register

| ID | Decision | Class | Settled |
|---|---|---|---|
| V-1 | Deltas with keyframes: a count bound and a large-delta rule | Blocking | here |
| V-2 | Delta element shape; positional arrays, with optional key matching | Blocking | here |
| V-3 | Reconstruction runs forward from a keyframe | Blocking | here |
| V-4 | Live image in the data chain; history behind `IVersionStore` | Blocking | here |
| V-5 | One version index per history collection, with a floor search | Blocking | here |
| V-6 | `previousVersion` addresses the head's node | Blocking | here |
| V-7 | One version per write, grouped by operation; never coalesced | Blocking | here |
| V-8 | Logical time orders versions; wall-clock time is recorded beside it | Blocking | here |
| V-9 | History is a tree; a restore adds a child of the restored version | Blocking | here |
| V-10 | Reads go through the current schema, and unmappable information is reported | Blocking | here |
| V-11 | Schema and relation history live in the history collection | Blocking | here |
| V-12 | Attribution belongs to the operation | Blocking | here |
| V-13 | The policy is persisted per collection; the default flips after measurement | Blocking | here |
| V-14 | Turning versioning off drops history explicitly, in the same call | Blocking | here |
| V-15 | Purge is defined on the tree, with its invariant | Blocking | here |
| V-16 | Related restore follows outgoing relations only | Blocking | here |
| V-17 | The erasure boundary | Blocking | here |
| V-18 | The assistant's journal holds version references for record changes | Blocking | here |
| I-1 | The default keyframe interval *k* | Implementation-time | Settled at step 5.2: *k* = 8, from the run of 2026-09-14 in `docs/benchmarks.md` ("Versioning — measurement"): history size is flat from *k* = 8 on the assistant-like workload and at 35% of full copy on small edits, with at most seven deltas and 1.25 ms per reconstruction; wide records edited one field at a time should override to 16 or 32 |
| I-2 | The large-delta ratio | Implementation-time | Settled at step 5.2: 0.5, from the same run: neutral on every workload, and the rule that keeps a complete rewrite at the cost of a full copy rather than twice it |
| I-3 | Whether a cumulative-bytes keyframe rule is added to the count rule | Future — see F-12 | — |
| I-4 | Flipping the default to `KeepVersions` | Implementation-time, with your sign-off | Settled at step 5.2 on 2026-09-14: yes, on the numbers of that run (an import at 2.3× the CPU and 166 bytes more file per row, edits at 1.9×, durable writes unchanged). `KeepVersions` at *k* = 8 and ratio 0.5 is the default for user collections created through `CreateCollection` and `CreateDatabase`; existing collections keep what they have |
| I-5 | Where an array column's element key is declared | Implementation-time | Settled at step 1.5: `ColumnDescriptor.ElementKey`, a field of the column's declaration in the collection's catalogue document (D-4), empty by default. Nested arrays have no declaration and are positional |
| I-6 | The cap on a related restore | Implementation-time | Settled at step 6.3: 1 000 records (`DbEntities.DefaultRelatedRestoreCap`, a parameter of `RestoreAsOf`). The set is gathered in memory before anything is written, so the cap bounds that planning and the one transaction after it; exceeding it fails naming the cap and the size reached |
| I-7 | The purge batch size | Implementation-time | Settled at step 7.1: 256 record identifiers per batch (`TokkDbConnection.DefaultPurgeBatchSize`). A batch is only the identifiers read ahead of the one-record transactions that purge them — one range read, 16 bytes each in memory — so it bounds neither a transaction nor the purge, and 256 keeps a scan from being held open across more than a few dozen pages of changes to the index |
| F-1 | Turning versioning off with a recorded gap | Future | A "history gap" outcome for `GetAsOf` and a gap node |
| F-2 | Incoming relations, and finding a holder through history, in a related restore | Future | A temporal index over values |
| F-3 | Queries over a whole collection as of a moment | Future | A temporal index |
| F-4 | Merging branches | Future | A three-way delta merge and a conflict model |
| F-5 | Automatic retention by age or count | Future | Builds on purge |
| F-6 | Versioning the reserved collections | Future | The catalogue would have to be readable before its own history |
| F-7 | Undoing a structural change from history | Future | Re-adding a column with its values as of a moment, and `Rewrite` keeping removed values |
| F-8 | Insert nodes written at first supersede instead of at insert | Future | A way to keep the insert's attribution with no node; a draft revision if step 5.1 needs it |
| F-9 | Erasure below the file system | Future | Beyond any engine's reach (V-17) |
| F-10 | UI-1, the version view | Future | Everything it needs is in §3.2 |
| F-11 | An undo that never collides with an intermediate state | Future | Deferred constraint checks, or a transient-value protocol, in the engine (V-7's limit) |
| F-12 | A cumulative-bytes keyframe rule beside the count rule | Future | A workload on which the count rule and the large-delta ratio leave reconstruction cost unbounded in bytes; the benchmark of step 5.1 would gain a column. Deferred from I-3 |

### 4.2 The blocking decisions

**V-1 — History stores deltas, and a version is a keyframe for one of two reasons.** Each version
that is not a keyframe stores its delta from its parent. A version is a *keyframe*, whose full image
is kept once it leaves the head, when:
- it is a root; or
- its distance from its nearest keyframe ancestor would reach or exceed *k*; or
- its serialized delta is at least `largeDeltaRatio` of its serialized image.

A keyframe of any kind stores no delta: its image is the whole of what it stores. A diff between a
keyframe and its parent is therefore computed from the two images (RH-4).

*k* and the ratio are per-collection settings. Their defaults are I-1 and I-2. Both can be changed on a
collection that already has history. Nothing already stored is rewritten: each version keeps the bound
of the *k* in effect when it was written, and the next write under a smaller *k* becomes a keyframe as
soon as its distance reaches or exceeds the new *k*.

*Why:* D-5 left deltas against full copies open, and §2.4 — a claimed novelty of the work — argues
for deltas. Deltas alone would break VR-6: rebuilding the oldest of a thousand versions would take a
thousand steps, hence the count bound. And a complete rewrite of a record produces a delta nearly as
large as the record, which would make history *more* expensive than full copy for that version,
hence the second rule.

*Consequence:* setting *k* = 1 makes every version a keyframe, so every superseded image is kept and
no delta is stored — exactly the full-copy approach, with nothing added to it — and NFR-4's baseline
is the same code with a different setting. No version ever costs more than its image plus its node.
Under `KeepVersions` the data page gets a retired image's space back exactly as it does under `None`.

**V-2 — A delta element is `(path, operation, oldValue, newValue)`, and arrays are positional unless
a key is declared.** Both values are stored. The operations are `Add` and `Remove` for an object
field, `Replace` for a value, and `Insert`, `RemoveAt` and `Move` for an array element. Absent and
null are different on both sides.

By default an array is a sequence. Elements are aligned by their stored bytes, and a path's index is
a position. A collection may declare an **element key** for an array column. When every element on
both sides is an object with a distinct, non-null key, elements are matched by key: a matched element
that moved becomes a `Move`, and a matched element that changed is diffed inside. An element whose key
value itself changed has a new identity, so it is a removal and an insertion. The diff falls back to
positions when any key is missing, duplicated, or of a type that cannot be compared (an object or an
array). Either way, the delta records which matching it used.

*Why:* eq. 2.21 names both values. With both, auditing a version's delta needs no reconstruction, and
inverting a delta means reversing its elements and swapping old with new. Matching by position alone
turns a reordered author list, one of whom also changed, into whole removals and insertions. Matching
by key says what happened, but only where there is a key to trust — a missing or duplicated key is
exactly where guessing would be wrong.

*Consequence:* paths stay positional in both modes, so replay is exact whichever matching produced
the delta. A path is a sequence of segments, not a string, because a column name may contain a dot.

**V-3 — Reconstruction runs forward from a keyframe, and never backward across a schema change.** To
read version *v*: start from the image of its nearest ancestor that has one. For each node on the way
down, migrate the image to that node's schema version, using schema history (V-11), and apply the
node's delta.

*Why:* migrations run one way. `Remove` discards values and `Retype` can lose them, so a delta written
under schema *s* cannot be un-applied from an image migrated past *s*. Going forward is always sound,
because a node's schema version is never lower than its parent's.

*Consequence:* the root image has to be kept, so a record updated once costs one image. The saving
starts at the second update. Inverse deltas are used for diffs and audit, never as the route to an
image.

**V-4 — The live record stays one full image in the data chain, and history lives behind
`IVersionStore`.** The layout behind the contract is one history collection per versioned collection,
named `_history:` followed by the collection's identifier, linked through `HistoryCollectionId`, with
its pages belonging to it (DC-1).

*Why:* queries, indexes, relations, the planner and scans all read the live image. Keeping it whole
means none of them changes, and VR-13's scan never meets a history page. A collection of its own
makes the overhead measurable per collection (VR-10), and makes dropping or purging one collection's
history touch nothing else. Putting all of it behind one contract (§3.1) is what keeps a second
storage subsystem from leaking into five layers.

*Consequence:* if per-collection history proves costly in catalogue entries or page management
(R-11), a shared layout can replace it behind `IVersionStore` without touching layers 2 to 5.

**V-5 — The version index is one B+Tree keyed by `(recordId, versionId)`, and it has a floor
search.** Its root is a field of the history collection's catalogue document. `BPlusTree.Floor(key)`
returns the greatest entry at or below a key. It makes one descent, remembering the nearest subtree
to the left of the path, and at most one more descent into that subtree's rightmost leaf when the
first leaf holds nothing at or below the key.

*Why:* VR-10 asks for a B-tree-like version index, and Phase 5 of the engine plan built one.
`CompositeKey.Encode(recordId, versionId)` keeps a record's versions together in creation order. With
the tree's existing operations, "the last version at or before a moment" means walking every earlier
version of the record (§2.2, item 10). A floor search makes it two descents at most, with no change to
the page layout. A backward leaf chain would also work, but it is a page-format change — the one the
entity-query plan's Q-3 declined. If that plan adds the chain, `Floor` may use it; it does not need
it.

*Consequence:* listing a history is one range, finding a version is one descent, and as-of lookups
are bounded by twice the tree's height.

The index also holds every other document the store finds by identifier: one entry per operation
document, keyed `(operationId, operationId)`, so `Operation(id)` is one descent and no record's range
contains one — identifiers come from one monotonic source, so an operation's can never equal a
record's. Schema and relation nodes are not found by identifier at all: there are few, every
reconstruction needs all of them, and the store reads them into memory at open and after each schema
change, as the catalogues are read. Nothing else could serve, because a history collection carries the
reserved prefix and therefore has no primary index (§2.2, item 7): `FindLiveRow` on it is a scan.

**V-6 — `previousVersion` points at the head's node.** In a live image's header, `PreviousVersion`
holds the address of the history node for that image's own `VersionId`. Zero means the record has no
history.

*Why:* VR-11 reserved the pointer as the step from a live image into its past, assuming the past would
be an image. Under V-1 the replaced image usually no longer exists, and the node is where the way back
to it starts. That changes what the field means but not its layout.

*Consequence:* the pointer is checked before use, the way `LiveRowAt` checks an index entry, and falls
back to the version index when the check fails. A read never repairs it.

**V-7 — One version per write, grouped by operation, and never coalesced.** Every insert, update,
delete or restore that changes something creates exactly one node, inside the write's transaction.
An update whose delta is empty creates nothing. A restore always creates its node, even when its delta
is empty, because it changes the head. Its delta is computed from its parent — the restored version —
as reconstruction presents it at the current schema version, that is `Reconstruct(v)` migrated forward
through schema history (V-3), to the document actually written; never from the head it replaces, whose
values are not the parent's. Restoring the current head is refused (RB-3).

An **operation** is an outermost transaction that records at least one version. Its identifier is
minted by `RecordIdentity.Next()` when it records its first version, and every node it writes carries
that identifier. Each history collection the operation touches gets one operation document.

*Why:* coalescing writes to the same record inside a transaction would mean rewriting nodes before
commit, and would hide writes the unit of work really made. One version per write with nothing else,
though, makes an import of 10 000 rows look like 10 000 unrelated events, with its attribution
repeated on each. The operation keeps every write exact and groups them without losing anything. The
engine's transaction number cannot serve, because it starts again on every open (§2.2, item 9).

*Consequence for undo:* undoing a group of writes — an operation, or an assistant request made of
several — takes two passes inside one unit of work.
1. **Validate every affected record before anything is written,** collecting every blocker:
   - **touched since:** its head is no longer its last version in the group;
   - **the end state conflicts:** a restored value collides with a record outside the group on a unique
     column; a reference would no longer resolve; or a record the undo deletes is still referenced from
     outside the group.

   If anything blocks, refuse the whole undo and name every blocker.
2. **Replay every write in exact reverse order,** each restoring the version it replaced, without
   re-checking versions the undo itself produced. A constraint refusal during the replay aborts the
   whole undo atomically.

Checking each write as it is replayed would fail, because a restore creates a new version. Merging a
record's writes into one restore would also fail when they interleave with other records' writes on a
unique column: with X a→b, then Y c→a, then X b→c, restoring X once collides with Y, whichever of X's
positions it is placed at.

*The limit, stated:* validation checks the end state, but the replay passes through intermediate states.
An undo can therefore be refused at replay even though its end state is valid. For example, a record
went a→b→c inside the group, and outside it another record has since taken b. The refusal is atomic and
names the record. Avoiding it needs deferred constraint checks, which the engine does not have (F-11).

**V-8 — Logical time orders versions, and wall-clock time is recorded beside it.** A version's
**logical time** is its identifier's timestamp. It defines version order and is what
`GetAsOf(moment)` compares against. An operation's **recorded time** is the wall clock when it
recorded its first version, and it is what history shows people.

Every identifier the engine mints comes from `RecordIdentity.Next()`, and that source is seeded from a
durable **high-water mark**: the greatest identifier issued, kept on the `_collections` descriptor beside
`LastOwningCollectionId`, written by every outermost commit that has pages to write and has moved it, and
loaded at open — raised, never lowered — so that `Next()` never returns an identifier below one already
stored. A new version's identifier is, in addition, the greater of `Next()` and the successor of the
record's latest version identifier. So every identifier — record versions, schema and relation nodes,
operations — ascends in write order across restarts, whatever the clock does.

*Why:* within a process, identifiers from `RecordIdentity.Next()` never run backwards. So logical time is
a total order that agrees with write order, and "the head at a moment" has exactly one answer. The wall
clock can step back and disagree with write order, and then that question would have two answers. But
`RecordIdentity` forgets its last identifier when the process ends (§2.2, item 12): a restart with the
clock behind would mint identifiers below the record's head, and below the latest schema node, relation
node and operation, which are ordered by logical time too (RH-1, RH-9, V-16). The successor of the head
closes that for one record's versions only. The mark closes it for everything, and costs at most one page
per transaction: the catalogue page that holds it is usually dirty already, because the record count is
saved on every insert and retirement. It is written at commit rather than when an identifier is minted,
so that no identifier minted later in the same transaction escapes it.

*Consequence:* logical time is never earlier than wall-clock time. It is later only after the clock has
stepped back, in this process or before a restart, and by at most the size of the step. `History` flags
an operation whose recorded time is earlier than its predecessor's, so the step is visible rather than
hidden. Across records and collections, "as of a moment" agrees with write order, which a related restore
(V-16) and `SchemaAsOf` (RH-9) depend on.

**V-9 — History is a tree: a restore adds a child of the restored version, and the head is always a
leaf.** A delete adds a tombstone as the head's child. Restoring version *v* adds a node whose parent
is *v*, and the old head becomes the leaf of its own branch. A record's branches are its leaves. After
a purge a record's history may be a forest (V-15).

*Why:* VR-5 and eq. 2.23 require that restoring and then editing leaves newer versions intact and
reachable. Because every write adds a child either to the head or to the version being restored,
every version except the head has been superseded. Every keyframe that anchors a reconstruction has
therefore already kept its image, and V-1's bound holds on every branch.

**V-10 — Reads go through the current schema, and anything the schema cannot show is reported.**
`GetAsOf`, `Diff` and `Restore` present a version as the current schema describes it, following these
rules for each schema step between the version and now:

| Step | What happens to a value or a delta element |
|---|---|
| Column added | Nothing: the version lacks the field and reads as lacking it |
| Rename | The first path segment takes the new name |
| Retype, lossless for this value | Converted. Lossless means converting back gives the same stored bytes |
| Retype, lossy for this value | Not converted; reported as `Unmapped(RetypeLossy, column, original)` |
| Remove | Reported as `Unmapped(ColumnRemoved, column, original)` |
| Removed, then a new column added under the same name | The old values are `Unmapped(ColumnRemoved)`, never presented as the new column's |

The rules compose **step by step, oldest step first**. A value that passes one step is carried to the
next under the name and type that step gives it. The first step that cannot carry a value ends its
journey, and `Unmapped` names that step, the value as it was before that step, and the original. A
lossless retype followed by a lossy one is therefore reported at the second retype, not converted
halfway and then dropped.

Every read that maps returns its `Unmapped` list. `GetStoredAsOf` and `Diff(..., asStored: true)`
apply no mapping.

*Why:* DC-7 made every read path present the current schema, and a restore has to produce a record
that is valid today. But mapping a delta through a lossy retype or a removal can only lose or invent
information, so a mapping that did it silently would make history lie. A structured result makes the
loss something a caller can show, rather than something that disappears.

**V-11 — Schema history and relation history live in the history collection.** A versioned
collection's history collection holds:
- a **schema node** for every schema version: the full column declarations, uniqueness included, and
  the migration step that produced it;
- a **relation node** whenever a relation naming the collection is created or removed, carrying its
  declaration.

Both are written in the transaction of the change. Turning versioning on writes a schema node for the
current version, plus one for each migration step still in the catalogue, marked as having unknown
earlier declarations, and one relation node for every relation currently naming the collection, marked
as of unknown creation time. Reconstruction builds its migrator from schema nodes, never from the
catalogue.

*Why:* the migration log records renames, retypes and removals, but not what a column was declared as
when it was added, nor which relations existed. Draft 1 kept the log alive in the catalogue for
history's sake, which made `Rewrite` history-aware and made the `_collections` document grow with
history. Schema history kept beside the versions answers "what did this collection look like then",
and lets `Rewrite` go on dropping the catalogue's log exactly as it does today.

*Consequence:* `SchemaAsOf(collection, moment)` exists. A related restore follows relations as they
were declared at the moment. Nothing about the catalogue or `Rewrite` changes.

**V-12 — Attribution belongs to the operation.** A `VersionAttribution(author, cause, comment)` scope
stamps the operation document. `cause` is a `Ulid`: the assistant's request identifier today, an event
identifier once the event log exists. Nodes carry only the operation's identifier.

*Why:* VR-4 wants to know who made a change and why. VR-1 says the application does not construct
versions. And repeating attribution on every node of an import is pure write amplification. A scope
delivers the information once, without turning write methods into versioning methods.

*Consequence:* an operation outside any scope has an empty author and cause, which reads as "unknown"
rather than a guess. A comment is caller text and must not carry record values, because erasure does
not scrub operations (V-17).

**V-13 — The policy is persisted per collection, and new user collections become versioned by default
once the cost is measured.** `KeepVersions` becomes the default for user collections created through
`CreateCollection` and `CreateDatabase` at step 5.2, after step 5.1's measurements have been reviewed
and signed off (I-4). Until then, the default is `None`. Existing collections keep what they have, and
an empty stored policy reads as `None`. Reserved collections refuse `KeepVersions`.
`DbEntities.RetentionPolicy` becomes a read-only view of the catalogue. *Flipped at step 5.2 on
2026-09-14 (I-4).*

*Why:* VR-1 asks for history without application code, and VR-12 asks that switching the policy be
the only change needed to start keeping versions. But every versioned write also writes a node, an
index entry and sometimes an image, so the default is decided on numbers from realistic workloads
rather than on intent. A policy settable per handle is a second source of truth.

*Consequence:* if step 5.1's numbers are not acceptable, step 5.2 stops and reports instead of
flipping, and the decision returns to a draft revision. The assistant does not wait on this default:
it requires versioning on its own collections (V-18).

**V-14 — Turning versioning off drops history explicitly, in the same call.**
`SetRetentionPolicy(collection, None)` is refused while the collection has a history collection.
`SetRetentionPolicy(collection, None, dropHistory: true)` removes the whole history and sets `None` in one
transaction. The whole history means every node, operation document, schema and relation node, and the
version index, all through `IVersionStore.DropHistory`, so secure release clears them. Live records are
untouched. A header whose `PreviousVersion` now addresses nothing reads as "no history" through the
pointer check of V-6.

*Why:* history with an unrecorded gap would claim a lineage it does not have. Representing the gap
honestly needs a new node kind and a new `GetAsOf` outcome, for a use case nothing in this plan has.
The assistant never turns versioning off. F-1 records what the gap model would need.

Draft 4 said "purge first", and that could never happen. A purge keeps the present by design (V-15):
every record keeps its head at the purge moment, so no purge empties a history. Turning versioning off
therefore needs its own explicit removal. Putting it in the same call keeps the collection from ever
being `KeepVersions` with no history collection behind it.

**V-15 — Purge is defined on the tree, and its invariant is proved rather than assumed.** For one
record and a moment *T*:
1. Let *H* be the head at *T*, by logical time: a version or a tombstone, if the record existed then.
2. Keep *K* = every node created at or after *T*, plus *H*.
3. For every node in *K* whose parent is not in *K*, before anything is removed: give it an image by
   reconstructing it (the live head is marked a keyframe instead, its image being live), make it a
   root, **remove its delta**, and record the removed parent's identifier as `cutFrom`. The delta has to
   go: its old values are the removed parent's values, so keeping it would keep the very data the purge
   removes. A root has no delta anyway (HS-5).
4. Remove every node outside *K*, and their index entries. Schema and relation nodes are kept: they
   hold no record values. A purge transaction follows V-17's journal rule, so the before images of
   what it removed do not outlive its commit.

A purge of a whole collection ends with one pass, after the record batches, that removes every
operation document no remaining node references. A purge of one record and an erase leave operation
documents alone: they hold no record values (V-12), and finding the ones nothing references would be a
scan per record.

**Invariant.** (a) For every *t* ≥ *T*, `GetAsOf(t)` gives the same answer before and after. (b) Every
kept version reconstructs to the same document. (c) Every kept version reaches a node with an image
within its stored distance.

**Proof.**
- (a): the head at *t* ≥ *T* was either created at or after *T*, so it is kept; or it was created
  before *T* and stayed head from then until *t*, so it was head at *T* too and is *H*.
- (b) and (c): take a kept version *m* and walk its parents inside *K*. The walk ends either at an
  ancestor that already had an image — kept and unchanged — or at a node whose parent was removed,
  which step 3 gave an image computed from the tree before the purge. Every node on the walk is kept
  and unchanged, so the deltas applied are the same ones. That node is *m*'s old image ancestor or lies
  between it and *m*, so the actual distance can only have shrunk.

*Consequence:* a record's history can become a forest — for example, *H* plus a later restore of a
version older than *T*. `History` returns the forest, and each root shows where it was cut. A lone
tombstone with nothing after *T* is kept as a parentless root: it answers "deleted", and a restore is
refused as no longer kept. Per-record purge applies the same rule to one record. NF-6 model-checks the
invariant with purges interleaved into random histories.

*And what a purge removes is gone.* A value that existed only in removed versions is, after the purge
commits, in no kept node, no delta, no freed page (V-17) and no journal frame. A value the kept versions
still hold stays, because they hold it.

**V-16 — A related restore follows outgoing relations only.** Relations are taken as declared at the
moment (V-11). A referenced record is found through the target column's current index, and is accepted
only if its version at the moment held the same value. A relation whose holder cannot be verified —
including a holder deleted since — refuses the restore, naming the relation and the value. Records
that referred to the restored record are never changed.

*Why:* following an incoming relation means finding the records that held a value at a moment, which
needs a temporal index over values (F-2). Refusing a case is honest; half-restoring an object is not.

**V-17 — The erasure boundary.** Inside the boundary are the database file and its rollback journal
file. Every byte the engine releases is cleared when released:
- a slot, the tail of a slot beyond a record rewritten in place, a compacted run, an overflow page;
- an index entry, unique and secondary keys and truncated prefixes included;
- every page of a dropped index, whether the drop comes from `DropIndex`, a `SetColumns` rebuild, or
  `DropCollection`, which also clears its collection's primary index pages;
- a history node, an image, a delta — including every node of a history collection dropped with its
  collection.

The journal needs a rule of its own. A committed frame normally stays in the journal file until the
next transaction begins (§2.2, item 11). That is harmless for ordinary writes and fatal for erasure,
because the frame's before images are the erased values. So **a transaction that erased anything,
dropped a collection, purged history or dropped history discards its frame as soon as its commit record
is durable** — the frame is committed, so it has nothing left to say to recovery. Ordinary commits, a
`DropIndex` included, keep today's protocol and cost: the values an index held are still in the live
records.

**Guarantee:** after `Erase` commits, no byte sequence of an erased value remains in the database file or
its journal file, whether or not the connection is then closed. The same holds for every value of a
collection after `DropCollection` commits. Two things are stated rather than implied. The erased record's
identifier may remain in the assistant's change journal, which is an audit record and carries no value
(V-18, AJ-7). And a process killed after the commit record and before the discard leaves the frame in the
journal until the next open, which discards it before reading anything.

Outside the boundary, stated rather than implied:
- the process's memory, since a managed runtime copies strings and cannot be zeroised on demand;
- the operating system's page cache and swap;
- the file system's own journal, remapped and snapshotted blocks;
- backups and copies of the file;
- anything already sent to a model.

*Why:* "erase" is much stronger than delete, and a guarantee without a boundary is one no test can
check. The boundary is where the engine has control of every byte.

*Consequence:* secure release is a page-layer property, so it also protects ordinary deletes and
updates under `None`. The assistant widens its own boundary with its diagnostics (V-18).

**V-18 — The assistant's journal holds version references for record changes.** For inserts, updates
and deletes of records:
- **The journal entry.** A `DataChange` carries the request, the record, `PreviousVersionId`,
  `VersionId` and the disposition — no field values and no caps.
- **Structural changes.** Removing or retyping a field, or dropping a collection, keep today's shaped
  and bounded payload (F-7).
- **Undo.** Every affected record validated first, with every blocker named; then restores replayed in
  exact reverse order, atomically (V-7).
- **Reversibility of a record change.** `Reversible` when its collection has no unique column and takes
  part in no relation; otherwise `ReversibleWithConditions`. It becomes `NotReversible` only once the
  version it needs has been purged.
- **Which collections.** The assistant requires `KeepVersions` on every collection it writes to,
  whatever the engine's default.
- **The contract.** The assistant's storage contract gains the few version operations undo needs, and
  both backends implement them.
- **The trace.** Before-and-after tables are read from a diff of the two versions.
- **Erasure.** Erasing a record also clears the diagnostic payloads of the requests that changed it.
  Conversations are the user's own text and are outside, and the confirmation says so.

*Why:* old values then have one copy instead of two. No payload cap decides whether a record change
can be undone. "Touched since" becomes exact, catching an A→B→A edit that a content hash misses. And
TR-2b's three payload shapes collapse to one for records.

The rule also corrects a claim. The assistant plan says record inserts and updates are unconditionally
reversible, and they are not, even today:
- A's email goes x→y, then B takes x. Undoing A would put x back beside B.
- A's conference goes c1→c2, then c1 is deleted. Undoing A would point it at nothing.
- Undoing an import deletes a conference that a later expense now references, and the restrict rule
  refuses.

*Consequence:* the undo window is bounded by history retention, and a change's values are visible
while its versions are kept. The assistant's compensation and erasure steps depend on Phases 1 to 7 and Phase 9 of
this plan.

---
## 5. Requirements

Priority: **M** = must, **S** = should. The source in brackets is the engine-plan requirement or the
decision each one closes or implements. The tests that check what the guarantees of §6 mean to a
caller are separate from these, which check the mechanism.

### 5.1 The delta — layer 0 (group DL)

**DL-1 (M, VR-2, V-2).** For two documents, the system shall compute a delta whose elements are
`(path, operation, oldValue, newValue)`, with the operations `Add`, `Remove`, `Replace`, `Insert`,
`RemoveAt` and `Move`, and with an absent value distinct from a null one.
*AC:* changing one field of a 50-field document yields exactly one `Replace`, and the serialized delta
is under a tenth of the serialized record. Setting a field to null yields a `Replace`; removing it
yields a `Remove`. Equal documents yield an empty delta.

**DL-2 (M, VR-2).** A path shall be a sequence of field-name and array-index segments, stored as
segments and rendered for people as `a.b[3].c`.
*AC:* paths through fields named `a.b` and `x[0]` round-trip through storage and render
unambiguously. `product.manufacturer.address.city` addresses a value three objects deep.

**DL-3 (M, VR-3, V-2).** By default, array changes shall be positional. Elements are aligned on their
stored bytes by a longest common subsequence, with memory linear in the length of the arrays. Between
anchors, a gap of equal length on both sides is diffed element by element, and an unequal gap becomes
`RemoveAt` and `Insert` elements. A removed and an inserted element with identical bytes become one
`Move`.
*AC:* a middle insertion yields one `Insert`; a removal yields one `RemoveAt`; swapping two elements
yields a `Move` and no insert or remove; one field changed in the third object of an array yields one
`Replace` at `[2].field`. An array of 100 000 scalars with one insertion diffs in milliseconds.

**DL-4 (M, VR-3, V-2).** A delta shall be ordered so every element's indices are valid at the moment it
is applied. Applying `diff(a, b)` to `a` gives `b`, and applying its inverse to `b` gives `a`. "Gives"
means equal in canonical form: object fields in ordinal order, arrays in order, values compared by
stored bytes.
*AC:* a property test over at least 10 000 generated pairs holds in both directions, in positional and
in keyed mode. The documents are at least three levels deep, with arrays of objects, every implemented
value type, nulls and absent fields.

**DL-5 (M).** Value equality shall be byte equality of the stored value — never CLR equality and never
index-key equality.
*AC:* `1.50m` against `1.5m`, two `DateTime` values differing only in `Kind`, and `5` as an `Int`
against `5` as a `Long` each produce a `Replace`.

**DL-6 (M, D-4).** A delta shall be stored as an ordinary document value through the existing
serializer.
*AC:* a delta round-trips through `StoredRecordUtilities`, and one holding a 20 KB value is stored
through an overflow chain rather than refused.

**DL-7 (M, NFR-5).** Applying a delta shall check each element's `oldValue` against the value it finds,
and refuse a mismatch with a typed `DeltaMismatchException` naming the path and the version.
*AC:* after one byte of a stored old value is corrupted, reconstructing that version or any descendant
throws, naming the first version that fails. No partially applied record is returned.

**DL-8 (S, V-2).** When the diff options name an element key for an array path, and every element on
both sides is an object with a distinct, non-null value at that key, elements shall be matched by key.
A matched element that moved becomes a `Move`, and a matched element that changed is diffed inside.
Otherwise the diff is positional. The delta records which matching it used for each array.
*AC:* reordering three authors while one of them changes a field yields `Move` elements and one
`Replace`, with no whole-object removal. An author whose key value changed yields a `RemoveAt` and an
`Insert`. A duplicated key, a missing key, and a key holding an object each fall back to positions, and
the delta says so.

### 5.2 The version store — layer 1 (group HS)

**HS-1 (M, VR-1, V-13).** The retention policy, the keyframe interval and the large-delta ratio shall be
persisted per collection in its catalogue document. `SetRetentionPolicy` runs as a schema change.
*AC:* all three survive a reopen. A collection in a database written before this plan reads as `None`.
`KeepVersions` on a reserved collection is refused, and so are an interval below 1 and a ratio outside
(0, 1]. `DbEntities` has no setter for the policy, and `RetireRow` takes no policy argument: retirement
behaves the same under every policy, and only the seam asks the catalogue. On a collection with history,
lowering *k* from 32 to 4 rewrites no stored node; the next write whose distance would reach or exceed 4
becomes a keyframe; and every version keeps the bound of the *k* it was written under.

**HS-2 (M, V-4, DC-8).** Turning `KeepVersions` on shall create the collection's history collection in
the same transaction and record its identity in `HistoryCollectionId`. `DropCollection` drops it in the
same transaction, by retiring every node and operation document and dropping the version index, so that
secure release (RP-4) reaches all of them.
*AC:* a failure injected between writing the policy and creating the history collection leaves neither
after a reopen. History pages carry their own owning collection id. Neither adapter lists the history
collection. Dropping the collection removes both from the catalogue.

**HS-3 (M, §3).** Only the write seam and schema changes shall call `IVersionStore`'s recording
operations, and no type outside `TokkDb.Pages.Versions` shall read a node document or the version index.
Exactly four paths may change a versioned record's stored image, and each keeps history consistent:
- the write seam, which records a version;
- `Rewrite`, which changes representation and preserves anything it would lose (WV-8);
- `Erase`, which removes the record together with its history (RP-5);
- `DropCollection`, which removes the collection together with its history (HS-2).

*AC:* an architecture test over the compiled assemblies asserts the first two rules. The comment on
`RetireRow` names its actual callers. A source-level test lists every call site of `RetireRow`,
`UpdateRow`, `RewriteRow` and `FreeItem` in the engine projects against a recorded list that says, for
each site, why it cannot reach a versioned collection's records outside the four paths, and fails on any
site the list does not name.

**HS-4 (M, VR-10, V-5).** The version index shall be a B+Tree over
`CompositeKey.Encode(recordId, versionId)`, with its root in the history collection's catalogue
document. `BPlusTree.Floor(key)` returns the greatest entry at or below a key. The index shall also hold
one entry per operation document, keyed `(operationId, operationId)`, and schema and relation nodes
shall be held in memory from open and refreshed after every schema change, so that no lookup of any
document in a history collection scans it.
*AC:* with 10 000 records of 100 versions each, `Floor` reads at most twice the tree's height in pages,
whichever version it lands on, including one whose predecessor is in the previous leaf. Listing one
record's versions reads only its range. `Operation(id)` reads at most the tree's height. A
reconstruction reads no schema node page. A property test compares `Floor` with a linear search over
random keys.

**HS-5 (M, VR-4).** A version node shall carry:
- the kind: `Baseline`, `Insert`, `Update`, `Delete` or `Restore`;
- the parent (absent for a root);
- the operation;
- the schema version its delta was computed under;
- the distance from its nearest keyframe ancestor;
- the delta — absent for a root, a tombstone and any keyframe (V-1);
- for a keyframe that has left the head, the image and that image's schema version;
- for a restore, the head it replaced;
- for a purge root, `cutFrom`.

It carries nothing derivable: the record and the version come from its key and its document header,
logical time from the version identifier, and attribution from the operation. Its shape shall be
declared in the catalogue (DC-7).
*AC:* every field round-trips, and `DescribeSystemCollection` lists the history collection's columns.

**HS-6 (M, V-7, V-12).** An operation shall be recorded once in each history collection it touches, with
its identifier, recorded time, author, cause and comment, and with an entry in the version index (HS-4).
*AC:* an import of 10 000 records in one unit of work writes 10 000 nodes and one operation document.
Two units of work write two operations. A rolled-back unit of work leaves no operation.

**HS-7 (M, V-8).** Every version identifier — in a history node and in a record header, versioned
collection or not — shall be minted by `RecordIdentity.Next()`. The greatest identifier issued shall be
kept as a high-water mark on the `_collections` descriptor, written by every outermost commit that has
moved it, and loaded into `RecordIdentity` at open, so that no identifier minted after a reopen is below
one already stored. In a versioned collection, a new version's identifier is the greater of `Next()` and
the successor of the record's latest version identifier. Logical time is the identifier's timestamp, and
recorded time is the wall clock.
*AC:* 10 000 updates of one record within one millisecond produce strictly ascending identifiers. With a
clock that steps back during writes, identifiers still ascend, and each logical time is at least its
recorded time. After closing the database, setting the clock an hour before the last write, and reopening
in a new process, the next update's version still sorts after the head, and `Floor` finds it; a schema
node, a relation node and an operation written next each sort after every identifier stored before the
reopen. `RecordHeader.ForNewRecord` no longer calls `Ulid.NewUlid()`.

**HS-8 (M, VR-11, V-6).** A live image's `PreviousVersion` shall address the node of its own
`VersionId`. It is checked before use, and falls back to the version index when the check fails.
*AC:* after every write the pointer addresses the head's node. After a purge moves that node, the head
is still found. A record with no history has a zero pointer.

**HS-9 (M, VR-4, V-12).** A `VersionAttribution` scope shall stamp the operation of every outermost unit
of work that begins inside it.
*AC:* three writes in one unit of work inside one scope belong to one operation with that scope's cause,
and three units of work inside one scope are three operations that share it. A nested scope is refused
rather than silently ignored. An operation outside any scope has an empty author and cause.

**HS-10 (M, V-11).** Every schema change of a versioned collection shall record a schema node, and every
creation or removal of a relation naming it shall record a relation node, in the transaction of the
change. Turning versioning on records the current schema, every migration step still in the catalogue,
and every relation currently naming the collection.
*AC:* `SetColumns`, `CreateRelation` and `RemoveRelation` each add exactly one node, and a rolled-back
schema change adds none. On the step 0.2 fixture, turning versioning on records its pending rename and
its relation, and `SchemaAsOf` at the switch-on moment reports that relation.

### 5.3 Recording versions — layer 1 (group WV)

**WV-1 (M, VR-1).** An insert into a versioned collection shall write the live image and an `Insert` node
with no delta and a distance of zero, and copy no image into history.
*AC:* inserting 10 000 records adds 10 000 nodes and no image. `IStorage.Update` alone — with no
versioning call anywhere in the adapter — causes a second version to exist, which is VR-1's own
criterion.

**WV-2 (M, VR-2, VR-12, V-1).** An update shall, in order:
1. Compute the delta from the head, as the current schema reads it, to the new document, using any
   declared element keys. If the delta is empty, stop: no node, no new image.
2. If the head's node is a keyframe, copy the head's image into it.
3. Retire the head's image from the data chain.
4. Write a node whose parent is the head. If the delta is at least the large-delta ratio of the new
   image, the node is a keyframe with distance 0. Otherwise its distance is one more than its parent's,
   or 0, making it a keyframe, when that would reach or exceed *k*. A keyframe stores no delta (V-1);
   every other node stores the delta.
5. Write the new image, pointing at the node.

*AC:* the data chain's record count and free space match the same workload under `None`. A record's first
update stores the root image and one delta. A complete rewrite of a record, with the ratio at 0.5,
stores no delta. At *k* = 1 no node ever stores a delta.

**WV-3 (M, VR-8).** A delete shall copy the head's image into its node if that node is a keyframe, retire
the image, and add a `Delete` tombstone whose parent is the head.
*AC:* the deleted record is absent from `GetById`, `GetAll`, queries and every index, and the record count
falls. Its history lists the tombstone, and every earlier version still reconstructs.

**WV-4 (M, VR-12).** Superseding a head that has no node shall first write a `Baseline` node for it —
identified by the header's `VersionId`, its image copied, its operation that of the write superseding
it, flagged as having no recorded origin — and then the node for the change. `Rewrite` writes the same
node for a head that has none when it would otherwise lose something (WV-8).
*AC:* on the step 0.2 fixture, switching a populated collection on and updating one record gives that
record `Baseline` and `Update`, gives every other record nothing, and changes no page that holds only
untouched records.

**WV-5 (M, VR-6, V-1).** Reconstructing any version shall apply at most *k* − 1 deltas, where *k* is the
interval in effect when that version was written.
*AC:* for *k* = 1, 4, 8 and 32, over 200 updates of one record — some of them complete rewrites — plus a
restore that opens a second branch, no reconstruction reports more than *k* − 1 deltas. At *k* = 1 every
superseded version holds its image and no node holds a delta. Changing *k* partway through a history
keeps both bounds.

**WV-6 (M, VR-13).** At commit, the data chain shall hold only live images, and a history page shall
never be part of a data chain.
*AC:* scanning a collection whose records have ten versions each reads no page owned by its history
collection.

**WV-7 (M, TX-1).** The node, its index entry, the operation document and its index entry, any keyframe
copy, any schema node, the retirement and the new image shall commit in one transaction.
*AC:* with the fault injector failing at each write of an insert, an update with a keyframe copy, a
delete and a schema change, in turn, a reopen always finds either the state before or the state after,
and WV-10's invariants hold.

**WV-8 (M, VR-12).** No path shall change a versioned record's content without creating a version, and
no path shall discard stored information of a version, except the explicit removals whose purpose that
is: purge (V-15), erase (V-17), dropping history (V-14) and dropping the collection (HS-2).

`Rewrite` is the one path that changes a versioned record's stored image outside the seam. It keeps the
record's `VersionId` and `PreviousVersion`. But it is not only a change of representation: migrating the
stored image drops the values of removed columns and converts retyped ones. So before `Rewrite` migrates
a head whose stored image V-10's mapping would report anything `Unmapped` for, it stores that image in
the head's node. A head that has no node yet gets its `Baseline` node first (WV-4), the image goes
there, and the header is pointed at it — the one case in which `Rewrite` changes `PreviousVersion`, from
zero. A `Rewrite` after a removal therefore copies every affected record of a versioned collection into
its history once, which is the price G-6 states. A node's image, once stored, is never replaced; a later
keyframe copy keeps the earlier, more complete one.

*AC:* `Rewrite` on a versioned collection leaves every version list and head identifier unchanged, and
every pointer either unchanged or moved from zero to a new `Baseline`, and every version still
reconstructs. After a rename, a removal and a lossy retype followed by `Rewrite`, `GetStoredAsOf` on
each head still shows the removed and unconverted values, for records with a node and for records
without one. A head with nothing unmapped gains no image and no node. The in-place paths of
`SystemDocumentStore.Write` and `MigrateRow` are shown by test to be unreachable for a versioned
record's content other than through this rule.

**WV-9 (M, V-11).** A schema change shall not create record versions.
*AC:* renaming a column of a collection with 1 000 versioned records adds one schema node and no version
node.

**WV-10 (M, V-4).** At every commit, the version store shall satisfy four invariants:
1. Every live record of a versioned collection either has no history, with a zero pointer, or has a head
   node whose version equals its header's `VersionId`.
2. Every node has exactly one index entry, and every index entry addresses its node.
3. No record has two heads.
4. Every node that is not a head, and whose distance is 0, holds an image.

*AC:* `Verify` checks all four. The model-based test of NF-6 asserts them after every operation and
every kill point.

### 5.4 Reading history — layer 2 (group RH)

**RH-1 (M, VR-4).** `History(recordId)` shall return the record's version forest. Each node comes with
its parent, kind and schema version, and with its operation's recorded time, author, cause and comment.
The result also names the head, the leaves and every root's `cutFrom`, and flags an operation whose
recorded time is earlier than its predecessor's. It works for deleted records.
*AC:* the history of S-1 reads as an audit trail answering when, by whom and how each change was made,
and the writes of one unit of work are visibly one operation.

**RH-2 (M, VR-7, V-10).** `GetAsOf(recordId, versionId)` shall return the record at that version through
the current schema, with its `Unmapped` list. `GetStoredAsOf` returns the version as stored.
*AC:* each version of S-1 equals the document a reader saw right after its write committed. A version
from before a rename shows the old name through `GetStoredAsOf` and the new name through `GetAsOf`.

**RH-3 (M, VR-7, V-5, V-8).** `GetAsOf(recordId, moment)` shall find the head at that logical time with
one `Floor` search. When there is none, it says which of four reasons applies: not yet created, deleted,
before recorded history (the root is a `Baseline` or a purge root), or no such record.
*AC:* moments before the insert, between updates, after the delete and before a baseline each give the
right outcome. For a record with 1 000 versions, the lookup reads at most twice the tree's height.
Answers are monotone in the moment.

**RH-4 (M, VR-7, V-10).** `Diff(recordId, from, to)` shall return, with an `Unmapped` list:
- when `to`'s parent is `from` and `to` has a delta — a keyframe has none (V-1) — that delta mapped
  through the schema steps above its version;
- otherwise, the delta between the two reconstructions under the current schema.

`Diff(..., asStored: true)` returns the stored delta unmapped, or the diff of the two stored images.
*AC:* the diff of a parent and a child with a delta reads no image. The diff of two branch leaves is
correct. A diff across a rename shows changed values under the new name, with no `Remove`/`Add` pair
for the rename.

**RH-5 (M, V-3, V-11).** Reconstruction shall migrate the image to each node's schema version, using the
migration steps in schema history, before applying that node's delta.
*AC:* a history interleaving a rename, a retype and a removal with updates reconstructs every version,
including after `Rewrite` has cleared the catalogue's migration log.

**RH-6 (M, V-10).** The mapping rules of V-10 shall be implemented exactly, with each row a test.
*AC:* a rename, a lossless retype, a lossy retype, a removal, and a removal followed by a new column of
the same name each produce the outcome V-10's table states. A value renamed, then retyped losslessly, then
retyped lossily is reported once, naming the third step and carrying the value as the second step left it.
No element or value is dropped without appearing in `Unmapped`.

**RH-7 (M, QM-2b).** Every history read shall hold a catalogue read lease while it runs.
*AC:* a `SetColumns` started during a reconstruction waits for it to finish.

**RH-8 (S, UI-4).** A history read shall report the versions examined, the keyframe it started from, the
deltas applied, the pages read and the time taken.
*AC:* WV-5's bound and RH-3's page bound are asserted on this report, not on timings.

**RH-9 (S, V-11).** `SchemaAsOf(collection, moment)` shall return the columns and relations as declared at
that logical time, or "before recorded history".
*AC:* after adding a column, renaming another and creating a relation, each moment in between returns
what was declared then.

### 5.5 Restore — layer 3 (group RB)

**RB-1 (M, VR-5, V-9).** `Restore(recordId, versionId)` shall write that version, through the current
schema, as the new head, by going through the write seam. The result is a `Restore` node whose parent is
the restored version and which records the head it replaced. Its delta is computed from the restored
version as reconstruction presents it at the current schema version, never from the replaced head, and
the node is written even when that delta is empty (V-7). The call returns its `Unmapped` list.
*AC:* with v1 to v4, restoring v2 and then editing leaves two leaves, v4 and the edit. Both reconstruct,
WV-5's bound holds on both branches, and nothing was deleted. A restore made after a lossy retype
reconstructs, through V-3, to exactly what `GetById` returned right after it.

**RB-2 (M, VR-8).** Restoring any version of a deleted record shall bring it back under its original
identity.
*AC:* a deleted record, once restored, has the same `Ulid`, is back in every index, and its history shows
insert, updates, tombstone and restore.

**RB-3 (M).** A restore shall be refused with a typed error, changing nothing, when it would violate a
unique index or a relation, or when it names a purged version, a tombstone or the current head.
*AC:* each of the five refusals is a test that names what refused. The head, the history and the indexes
are unchanged afterwards.

**RB-4 (M, V-10, D-14).** A restore shall return, as `Unmapped`, every value the current schema could not
take.
*AC:* restoring a version that held a value in a since-removed column returns the record without it, plus
`Unmapped(ColumnRemoved)` naming the column and carrying the value.

**RB-5 (S, VR-9, V-16).** `RestoreAsOf(recordId, moment, followRelations: true)` shall restore the record,
plus every record reachable from it through outgoing relations as declared at that moment, in one
transaction and under a cap (I-6). A holder is found through the target column's current index and
accepted only if its version at the moment held the same value.
*AC:* a publication and its author, both edited since a moment, are restored together to that moment. A
relation created after the moment is not followed. A holder that cannot be verified — including one
deleted since — refuses the whole restore, naming the relation and the value. A cycle terminates.
Exceeding the cap fails before anything is written. No record referring to the restored one is changed.
Built by step 6.3, which runs after Phase 8 (§9).

### 5.6 Retention and erasure — layer 4 and secure release (group RP)

**RP-1 (M, V-15).** `PurgeHistory(collection, before)` shall apply V-15's rule to each record, one record
per transaction, in batches (I-7), and shall be safe to resume; it ends with one pass that removes every
operation document no remaining node references. Before committing a record, the purge checks that
record's kept forest: WV-10's invariants hold, and every kept node's parent is kept, or the node is a root
holding an image or the live head. If the check fails, it rolls that record back and reports it rather
than committing a history it cannot rebuild.
*AC:* history size falls. Interrupting a purge with the fault injector and running it again gives the same
result as one run. A purge given a deliberately broken rerooting (a test hook that skips one image)
rolls that record back, reports it, and leaves its history as it was.

**RP-2 (M, V-15).** The purge invariant shall hold.
*AC:* over histories with branches, restores of versions older than the moment, deletes and schema changes:
- for every sampled *t* ≥ `before`, `GetAsOf(t)` is identical before and after;
- every kept version reconstructs identically;
- every kept version reaches an image within its stored distance;
- a record whose kept nodes form two trees returns a forest, with `cutFrom` on each root;
- every rerooted node has no delta;
- right after the purge commits, a distinctive value that existed only in removed versions is found in
  neither the database file nor its journal.

**RP-3 (M, V-15).** `PurgeHistory(recordId, before)` shall apply the same rule to one record, and
removes no operation document.
*AC:* purging one record leaves every other record's history byte-identical.

**RP-4 (M, V-17).** The page layer shall clear every byte it releases, whatever the retention policy:
- a freed slot;
- the bytes of a slot beyond a record rewritten in place, from the new length to the slot's end;
- the run a compaction vacates;
- a freed overflow page;
- the entries an index page removes, and the space a split or merge leaves behind;
- every page of an index that is dropped — by `DropIndex`, by a `SetColumns` rebuild, or by
  `DropCollection`, which also clears its collection's primary index pages.

*AC:* after updating, deleting and compacting under `None`, a search of the file finds no retired value.
After a system document is rewritten in place with a shorter document, the rest of its slot reads back
cleared. After `DropIndex` on a column, every page the index occupied reads back cleared, apart from its
page header.
After `DropCollection` on a versioned collection with a secondary index, a search of the file and its
journal finds none of the collection's values, current or historical. A benchmark reports the cost
(NF-5).

**RP-5 (M, V-17).** `Erase(recordId)` shall remove the live image, every node of the record — tombstone
included — and every index entry for it, in one transaction. Operation documents are left: they hold no
record values (V-12), and the collection's next purge removes the ones nothing references. That
transaction discards its journal frame as soon as its commit record is durable (V-17).
*AC:* a record carries a distinctive string in a live field, in three past versions and in a secondary
index key. Right after the erase commits — with the connection still open and no further transaction —
neither the database file nor its journal file contains the string, and `GetAsOf` reports no such record.
An erase inside a larger unit of work discards the frame of that unit of work's commit. An ordinary
commit still leaves its frame in place, as today.

**RP-6 (M, V-14).** Changing a collection from `KeepVersions` to `None` shall be refused while it has a
history collection, unless the same call asks to drop the history. With `dropHistory: true`, the history
is removed and the policy set in one transaction.
*AC:* the refusal names the collection and `dropHistory`. With the flag, the history collection is gone,
every live record reads as before, and `History` reports no history for any record. A distinctive value
that existed only in past versions is, right after the commit, in neither the database file nor its
journal. A failure injected in the middle leaves both the policy and the history as they were. Turning
versioning back on afterwards gives a record a `Baseline` at its next change.

**RP-7 (S, VR-10, UI-4).** `HistoryReport(collection)` shall give the counts of nodes, keyframes and
operations, the bytes of deltas and images, the pages, and the oldest schema version referenced.
*AC:* the numbers agree with a scan, and NF-2, NF-4 and NF-5 are computed from them.

**RP-8 (S, NFR-5).** `VerifyHistory(collection)` shall report the first node that breaks any of three
things: an invariant of WV-10, a reconstruction that fails DL-7's check, or a stored distance smaller than
the actual one. A larger stored distance, which a purge leaves behind, is allowed.
*AC:* each kind of deliberate damage is reported at the right node, and a clean history reports nothing.

### 5.7 The assistant — layer 5 (group AJ)

**AJ-1 (M, V-18).** A `DataChange` for a record insert, update or delete shall carry the record,
`PreviousVersionId` (null for an insert) and `VersionId`, and no field values. A structural change keeps
today's payload and its bounds.
*AC:* importing 10 000 records produces change records of fixed size. A `_dataChanges` document written
before this change still reads, with null versions. The payload tests for structural changes pass
unchanged.

**AJ-2 (M, V-18).** The assistant's storage contract shall offer six version operations, implemented by
`TokkDbStorage` and `MemoryStorage` alike and checked by one contract suite:
- `HeadVersion`;
- `Keeps`;
- `DiffVersions` — column by column, old beside new;
- `RestoreVersion`;
- `PurgeRecordHistory`;
- `Erase`.

*AC:* the suite passes against both backends. `MemoryStorage` keeps a full copy per version, which is
honest for a fake: no semantics differ, only cost. The contract project still references nothing.

**AJ-3 (M, V-18).** Every collection the assistant creates shall be `KeepVersions`, and opening an
assistant storage shall switch any user collection still at `None` to `KeepVersions`, without rewriting
records.
*AC:* a collection created through the assistant reports `KeepVersions` whatever the engine's default. A
database with a `None` collection is switched on at open, and its records gain `Baseline` nodes only when
first changed.

**AJ-4 (M, V-7, D-17, AG-11a, AG-11b, AG-11f).** Compensation of a request shall follow V-7's two passes
in one unit of work.
1. **Validate every affected record before any write, collecting every blocker:**
   - its head is no longer the `VersionId` of the request's last change to it;
   - its end state — its values in the version it would be restored to, computed from its current
     values and `DiffVersions` — collides on a unique column with a record the request did not change;
   - a reference it would hold does not resolve in the end state;
   - it would be deleted, and a record the request did not change still refers to it.

   If anything blocks, refuse the whole undo and name every blocker, before anything is written. The pass
   takes any set of the request's changes, not only all of them, so the assistant validates a partial
   undo the same way before offering it (its AG-11b). A record left out counts as "not changed by the
   request" for the conflict checks.
2. **Replay** the request's changes in exact reverse order. Each restores its `PreviousVersionId`, or
   deletes the record when that is null. Versions the compensation itself produces are not re-checked. A
   constraint refusal during the replay — possible only against an intermediate state, V-7's stated
   limit — aborts the whole undo atomically and names the record.

*AC:*
- a request that updated one record twice is undone;
- a request that changed a unique column as X a→b, Y c→a, X b→c is undone;
- a record changed A→B→A by a later request counts as touched;
- undoing an import after an unrelated record changed still works;
- an undo blocked by two records — one touched since, one holding a unique value the undo would restore —
  is refused before any write, naming both;
- a record changed a→b→c by the request, whose intermediate b has since been taken by an outside
  record, is refused atomically at replay, naming that record, with storage unchanged.

**AJ-5 (M, V-18).** The reversibility of a record change shall be computed before it runs, as
`Reversible` if its collection has no unique column and takes part in no relation, and as
`ReversibleWithConditions` otherwise. At compensation, a change whose version is no longer kept is
refused as `NotReversible`.
*AC:* the three counterexamples of V-18 are tests that refuse whole and name what blocks them. On a
collection with neither a unique column nor a relation, undo never refuses unless the record was touched
or its version purged.

**AJ-6 (M, TR-6).** The before-and-after table of a record change shall be read with `DiffVersions`.
*AC:* the table shows every changed column old beside new. After the versions are purged, it says the
values are no longer kept, and the request and time are still shown.

**AJ-7 (M, V-17, V-18).** Erasing a record through the assistant shall run the engine's erase, and clear
the step input and output payloads of every request whose `DataChange` names the record, in one unit of
work. Conversations are outside, and the confirmation says so.
*AC:* after the erase, a search of the database file and its journal finds the distinctive string only in
a conversation entry the user typed, and the confirmation card states that conversations are kept.

**AJ-8 (S, V-18).** The assistant's history purge shall never be later than the start of its compensation
window.
*AC:* a configuration that would purge inside the window is refused, naming both moments.

### 5.8 Non-functional (group NF)

**NF-1 (M, NFR-2).** Reading any version shall take under 50 ms at the chosen interval, over 100 000
records with 10 versions each, on the reference machine.
*AC:* the benchmark reports the distribution and the worst case.

**NF-2 (M, NFR-4, V-1).** Delta history shall be compared with full copy (*k* = 1) on the same operation
sequences: small edits to wide records, appends to arrays, and complete rewrites. Each workload is run
with the large-delta rule on and off.
*AC:* the benchmark reports the size ratio for every combination, including the ones where deltas lose.

**NF-3 (M, VR-6).** Reconstruction time and history size shall be reported against *k* = 1, 2, 4, 8, 16
and 32, and against the large-delta ratio, for each of four workloads:
- the synthetic ones of NF-2;
- the benchmark project's `Publication` documents, with nested authors and keyword arrays;
- an assistant-like history: an import followed by scattered corrections;
- a wide-record edit history.

*AC:* the defaults chosen for I-1 and I-2 are recorded with the workload they were chosen for, and with
the cost of the same defaults on the other workloads, so a collection whose workload differs knows what
to override.

**NF-4 (M, VR-10).** History size shall be reported against the number of versions, from 1 to 100 per
record.

**NF-5 (M, V-13).** Write amplification shall be reported per operation — pages dirtied, journal bytes,
file growth, and durable and bulk latency — under `None` and `KeepVersions`, with 0 and 3 secondary
indexes, and with secure release measured separately. Three workloads:
- an assistant-like import of 10 000 rows of 12 columns in one unit of work;
- small edits to wide records;
- a mix with deletes.

**NF-6 (M, NFR-8).** A model-based test shall run seeded random sequences of at least 1 000 operations —
inserts, updates, deletes, restores, purges and schema changes — over at least 50 records. After every
operation it asserts every version against an in-memory model, the tree shape, WV-10's invariants and
V-15's invariant. It then repeats with at least 100 fault-injected kill points.
*AC:* every assertion holds. After each kill point, history equals the model at the last committed
operation. A failure prints its seed.

**NF-7 (M, ST-9).** No page layout and no root-page format version shall change.
*AC:* the step 0.2 fixture opens, reads, and starts versioning when switched on, and
`RootPage.CurrentFormatVersion` is still 2.

---

## 6. Guarantees

§5's acceptance criteria check that the mechanism does what is specified. These check that what is
specified is what a caller needs. Each guarantee is a promise stated in terms a user of the public API
can observe, and each has its own test.

The guarantee tests go through the public surface of §3.2 only. They never inspect a node, a page or the
version index, so they stay valid if the mechanism behind them changes. They live in
`VersioningGuaranteeTests` for the engine and `UndoGuaranteeTests` for the assistant.

**G-1 — A unit of work is the boundary.** Everything a committed unit of work changed appears in history
as one operation. A rolled-back unit of work leaves nothing in history. No reader ever reads a version of
a write it cannot yet see.

**G-2 — History tells the truth.** Reading a version as stored returns exactly what `GetById` returned
right after that version's write committed. Reading it through the current schema returns that same
document mapped by V-10's rules, with everything the mapping could not carry reported. A write that
changed nothing adds no version, and a write that changed something adds exactly one.

**G-3 — Time never runs backwards.** `History` lists versions in write order. `GetAsOf(t)` gives answers
that are monotone in *t*, and a wall clock that steps back changes neither.

**G-4 — Restore never destroys.** After any restore, every version that could be read before can still be
read, unchanged. The restored record equals the version restored, except for what the restore reports as
unmapped.

**G-5 — Branches are first-class.** Every leaf can be read and restored. Restoring or editing one branch
changes no version on another.

**G-6 — Schema evolution never makes history unreadable, or silently lossy.** After any sequence of schema
changes and `Rewrite`, every version can still be read. Anything the current schema cannot show is
reported as unmapped, and `GetStoredAsOf` still shows it.

**G-7 — Purge keeps the present and everything after the moment.** For every *t* at or after `before`,
`GetAsOf(t)` gives the same answer after a purge as before it. Every version created at or after `before`
reads the same.

**G-8 — Erase is total within its boundary.** Once an erase has committed — whether or not the connection
is then closed — no byte of an erased value remains in the database file or its journal. The record's
identifier may remain in the assistant's change journal, and a process killed between the commit record
and the discard leaves the frame until the next open removes it (V-17).

**G-9 — Turning versioning on changes nothing already stored.** Every record reads the same afterwards, and
no page holding only untouched records changes.

**G-10 — Undo returns to exactly before, or refuses whole.** After a request is compensated, every record it
changed reads as it did before the request. If any record cannot, nothing changes, and every record that
blocks the undo is named.

---

## 7. The scenarios that define "done"

**S-1 — The §2.4 scenario.** A publication is inserted, its title corrected, its year changed and an author
inserted into the middle of its author array, then it is deleted and restored to its last state. History
lists `Insert`, `Update`, `Update`, `Delete` and `Restore`, with their operations and attribution.
`GetAsOf` returns each state. `Diff` between the two updates shows one `Replace` and one `Insert`. The
restored record has its original identity.

**S-2 — A branch.** A record has four versions. The second is restored and then edited. The record has two
leaves, both readable, and the diff between them is correct. Nothing was removed.

**S-3 — History across schema changes.** Updates are interleaved with adding a column, a rename, a lossless
retype, a lossy retype and a removal, and then `Rewrite` runs. Every version reads through the current
schema, with the lossy and removed values reported as unmapped, and as stored. `SchemaAsOf` answers for
every moment in between.

**S-4 — Turning it on for an existing database.** The step 0.2 fixture is switched on and one record
updated. That record has `Baseline` and `Update`, the others nothing, and no untouched page changed.

**S-5 — The bound.** With *k* = 8 and 100 updates, some of them complete rewrites, no reconstruction applies
more than seven deltas.

**S-6 — Purge with branches.** A record is edited, one of its old versions is restored after the purge
moment, and it is edited again. After `PurgeHistory(before)`, its history is a forest of two roots, and
every answer for a moment at or after `before` is unchanged.

**S-7 — Erase.** A distinctive string is found nowhere in the database file or its journal after the record
carrying it is erased through the assistant — except in a conversation entry the user typed, which the
confirmation said would be kept.

**S-8 — Undo from history.** The assistant deletes a record too wide for any journal cap, and undoing the
request brings back every field. In a second run, an A→B→A edit by a later request blocks the undo, and
the record is named.

**S-9 — Related restore.** A publication and its author, both edited since a moment, are restored together
to that moment. A relation created after the moment is not followed.

**S-10 — An import is one operation.** The assistant imports 10 000 rows in one unit of work. History shows
one operation for all of them, the journal's size does not depend on the width of the rows, and undoing
the request removes exactly the imported records.

### The ones that are not happy paths

- **N-1.** `KeepVersions` on a reserved collection is refused.
- **N-2.** Changing `KeepVersions` to `None` with history present, without `dropHistory`, is refused, naming
  the flag.
- **N-3.** A restore to a purged version is refused as no longer kept.
- **N-4.** A restore that violates a unique index is refused, naming the column and the holder, and nothing
  changes.
- **N-5.** A restore to a tombstone is refused.
- **N-6.** A corrupted stored old value raises `DeltaMismatchException` naming the version and the path, and
  `VerifyHistory` reports the node.
- **N-7.** A kill between retiring the head and writing its node leaves the state before, with the
  invariants holding.
- **N-8.** `Rewrite` after a rename clears the catalogue's log, and every version still reads.
- **N-9.** Moments before the insert, after the delete and before a baseline, and any moment for an erased
  record, give four different answers.
- **N-10.** A clock that steps back during writes, or before a reopen, leaves version order and as-of
  answers monotone, for records and for schema nodes alike, and `History` flags the step.
- **N-11.** A related restore whose holder cannot be verified is refused, naming the relation and the
  value.
- **N-12.** Restoring a version whose column has since been removed succeeds without the value, and reports
  it as unmapped.
- **N-13.** A diff across a lossy retype reports the old values as unmapped rather than converting them.
- **N-14.** The assistant's undo of an update, after another record has taken the unique value the undo
  would put back, is refused whole, naming the other record.

---

## 8. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **R-1** Versioning adds write amplification: a node, an index entry, an operation, sometimes an image | Imports become measurably slower and the file grows | Operations carry attribution once (V-12); large deltas become images (V-1); NF-5 measures realistic workloads, and the default flips only after sign-off (V-13). F-8 is the prepared fallback for inserts |
| **R-2** Schema and relation nodes are never purged | History collections keep a little metadata forever | A node per schema change is tens of bytes and holds no record values; `HistoryReport` counts them |
| **R-3** The tree, keyframe or purge logic is wrong in a case no example covers | A past version silently reads as something it never was | DL-7 verifies every old value as it is applied; NF-6 model-checks every version and both invariants; RP-8 checks a real file; V-15's proof names what the tests must cover |
| **R-4** The entity-query plan is editing `DbEntities`, `TokkDbConnection` and `QueryService` right now | Merge conflicts where the seam lives | Phases 0 and 1 touch none of those files. Start Phase 2 once entity-query Phase 1 has landed |
| **R-5** `MemoryStorage` and `TokkDbStorage` drift apart on version semantics | Orchestration tests pass against a fake that behaves differently | One contract suite runs against both (AJ-2), and G-10 runs against both |
| **R-6** Canonical equality ignores the order of object fields | A caller comparing raw bytes sees a difference that is not one | Stated in DL-4; the document model addresses fields by name |
| **R-7** The wall clock steps back | Recorded times disagree with order | V-8: order and as-of use logical time, seeded from a durable mark so a restart cannot reorder them; recorded time is shown and a step back is flagged |
| **R-8** Tests written for `None` fail when the default flips | Time spent on failures that are not regressions | Step 3.1 names `None` in every such test before step 5.2 |
| **R-9** An element key is declared that is not really an identity | A diff reads as moves that did not happen | Replay stays exact, because paths are positional; duplicated or missing keys fall back to positions, and the delta says which matching it used |
| **R-10** Clearing released bytes costs every write something | Latency moves | The pages are already dirty in the transaction, so the cost is memory writes, not I/O; NF-5 reports it separately |
| **R-11** One history collection per collection multiplies catalogue entries and page bookkeeping | Open time and catalogue size grow with the number of collections | `DatabaseOpenBenchmark` runs with versioning on in step 5.1; the layout can change behind `IVersionStore` (V-4) |
| **R-12** The assistant's undo now depends on this plan | Assistant step 4.7 cannot finish before Phases 1–7 and 9 | Structural undo and every other part of the assistant are unaffected; the dependency is stated in both plans |
| **R-13** Purging a large collection takes a long time | An administrative purge blocks writers for long stretches | One record per transaction, in batches, resumable (RP-1) |
| **R-14** An undo whose end state is valid is refused against an intermediate state | A person is told an undo cannot run, although the result would have been consistent | Rare — it needs a value changed twice inside one request and its middle value taken since. The refusal is atomic and names the record (AJ-4); F-11 records what would remove it |
| **R-15** Discarding the journal frame after an erase changes the commit protocol | A mistake here could lose recoverability for the erase transaction | The frame is discarded only after the commit record is durable, when recovery would discard it anyway; only transactions that erased anything or dropped a collection take the path; step 7.3 tests recovery after an ordinary commit, an erase and a drop |
| **R-16** Clearing a dropped index or collection dirties every page it occupied | `DropIndex`, `SetColumns` rebuilds and `DropCollection` write a journal frame proportional to what they drop | These are rare schema changes, and `DropCollection` already retires every record one by one today, so the order of its cost does not change; step 7.2 measures it for NF-5 |

---

## 9. Development plan

**Phase 0 — Before anything changes.** Record the decision in the engine plan, and check in a database file
written by the engine as it is now.
*Exit:* D-5, VR-12 and Phase 8 of `requirements-and-plan.md` point here, and the fixture opens in a test.

**Phase 1 — The delta (layer 0).** Paths, elements, diff for objects and arrays (positional and keyed),
apply with verification, invert, canonical equality, storage as a document value.
*Exit:* DL-1 to DL-8 hold.

**Phase 2 — Version store foundations (layer 1).** Identifiers and clocks, the persisted policy, the history
collection and the `IVersionStore` contract with its architecture test, nodes, operations, the version
index with `Floor`, attribution, and the recording of schema history. The default stays `None`, and no
record version is written yet.
*Exit:* HS-1 to HS-10 hold for what Phase 2 builds. The clauses that need record versions are checked
where they arrive and named there: HS-1's change of *k*, HS-6's import and HS-7's reopen at step 3.2, and
HS-8's purge at step 7.1.

> **Entry gate for Phase 3.** Every blocking decision in §4.1 is settled, and the step 0.2 fixture exists.
> A step that meets a question which would change what is stored, or what a guarantee means, stops and
> raises it as a blocking decision. It never settles such a question in code.

**Phase 3 — Recording versions (layer 1).** Tests that mean `None`; insert and update with both keyframe
rules; delete and baseline; atomicity, invariants and `Rewrite`.
*Exit:* WV-1 to WV-10 hold — WV-5 on stored distances until step 4.1 checks it on reconstruction
reports — and S-4 and N-7 pass.

**Phase 4 — Reading history (layer 2).** Reconstruction with its report, the version forest, as of a
version and as of a moment, schema as of a moment, and diff with the mapping rules.
*Exit:* RH-1 to RH-9 hold, and S-3, S-5, N-8, N-10 and N-13 pass, with N-9 apart from its
erased-record clause, which step 7.3 adds.

**Phase 5 — Measure, then decide the default.** Interval, large-delta ratio and write amplification on
realistic workloads; then the default, only with sign-off.
*Exit:* the numbers are in `docs/benchmarks.md`, I-1, I-2 and I-4 are recorded in §4.1 with their
evidence, and the default is either flipped or explicitly left at `None`.

**Phase 6 — Restore (layer 3).** Restore as a child, restoring the deleted, refusals and unmapped values.
Related restore along outgoing relations is step 6.3, deferred until after Phase 8.
*Exit:* RB-1 to RB-4 hold, and S-1, S-2, N-4, N-5 and N-12 pass. N-3 needs a purge and passes at step
7.1; RB-5, S-9 and N-11 pass at step 6.3.

**Phase 7 — Retention and erasure (layer 4).** Purge per collection and per record, secure release, erase,
refusing to turn versioning off, the report and verification.
*Exit:* RP-1 to RP-8 hold, and S-6, the engine half of S-7, N-1, N-2, N-3, N-6 and the rest of N-9 pass.

**Phase 8 — Guarantees and the whole engine.** The model-based test, the guarantee tests, compatibility, the
full benchmark run, and traceability.
*Exit:* NF-1 to NF-7 and G-1 to G-9 hold, and §11's table names a test for every requirement whose step
has run; RB-5 waits for step 6.3.

**Phase 9 — The assistant (layer 5).** Journal references, version operations on both storages, compensation
and reversibility, the trace and erasure.
*Exit:* AJ-1 to AJ-8 and G-10 hold, and S-7, S-8, S-10 and N-14 pass.

**Critical path for the dissertation:** Phases 1, 3, 4, 6 and 8. Phase 2 is the scaffolding they stand on,
Phase 5 decides what the default costs, Phase 7 is what makes a history that grows forever defensible,
and Phase 9 is where the assistant stops needing its journal to be the only copy of anything. Step 6.3,
the related restore, is off the path: VR-9 is a should, the step is the largest single one with the least
weight in the dissertation, and nothing in Phases 7 to 9 depends on it, so it runs after Phase 8.

---

## 10. Development steps, as prompts for Claude Code

One step per message, each written to be pasted as it stands. Code follows the project it lands in: in
the engine, two-space indentation and `//` comments that give the reason; in `TokkDb.Assistant.*`,
four-space indentation and XML documentation. Put requirement identifiers in comments where a reader
would ask why. Before saying a step is done, run the suites of every project it touched — **and, for any
step that touches `TokkDb.Pages`, `TokkDb.Documents` or `TokkDb`, every test suite in the solution**,
because every other project depends on them. Say which tests changed and why. **A step that changes
behaviour corrects, in the same step, every code comment, XML documentation comment and document that
describes the old behaviour** — §2.2 item 13 lists the known ones — and names them in its message.
**If a step meets a question that would change what is stored or what a guarantee means, stop and raise
it instead of deciding it in code** (§9, the Phase 3 gate).

### Phase 0

**0.1 Record the decision in the engine plan**
> Read docs/versioning-requirements-and-plan.md sections 2 to 4. Edit docs/requirements-and-plan.md and
> nothing else:
> - in D-5, mark the open question (deltas or full copies) as settled by V-1 of the versioning plan, with
>   a link;
> - under VR-12, add a note that V-1 and V-6 change what KeepVersions retains and what previousVersion
>   addresses, with the layout unchanged;
> - replace Phase 8's bullet list with a paragraph that points to the versioning plan and names its ten
>   phases.
>
> Done when: the three edits are in, and git diff shows no other paragraph changed.

**0.2 A fixture from the engine as it is**
> Read docs/versioning-requirements-and-plan.md NF-7, G-9 and S-4. Before any versioning code exists,
> write a small generator in TokkDb.Tests that uses the current engine to create a database with two
> user collections of about 50 records each. It should include one record updated three times, one
> deleted, a unique index, a relation, and a column renamed without Rewrite. Put the file under
> TokkDb.Tests/Fixtures, copied to the test output, with a README naming the commit that wrote it and
> saying it must never be regenerated. Add a test that copies it to a temporary file, opens it, and reads
> every record. Done when: the fixture is in the repository and the test passes.

### Phase 1

**1.1 Paths and elements**
> Read docs/versioning-requirements-and-plan.md DL-1, DL-2, DL-6, V-2 and §3.2. In TokkDb.Documents, add
> a Delta folder with:
> - DeltaPath, made of field-name and array-index segments;
> - DeltaOperation: Add, Remove, Replace, Insert, RemoveAt, Move;
> - DeltaElement: path, operation, old value and new value, with an absent marker distinct from
>   NullDocumentValue;
> - DocumentDelta: an ordered list of elements, which records for each array whether it was matched by
>   position or by key.
>
> Render paths as a.b[3].c, quoting names that contain a dot, a bracket or a quote. Store them as
> segments, never as the rendered string. Convert a DocumentDelta to and from an ArrayDocumentValue, so
> it is stored through the existing serializer with no new binary format. Done when: paths through
> fields named "a.b" and "x[0]" round-trip and render unambiguously; absent and null survive a round
> trip as different things; and a delta holding a 20 KB string round-trips through
> StoredRecordUtilities.

**1.2 Diff for objects and values**
> Read docs/versioning-requirements-and-plan.md DL-1, DL-4 and DL-5. Add CanonicalValue.Equal: values
> compare by serialized bytes, objects as maps with fields in ordinal order, arrays in order. Add
> DocumentDiff.Compute for everything except arrays, which step 1.3 adds: recurse into objects; emit Add
> or Remove for a field on one side only, and Replace for a changed value, including a change of type;
> emit an object's fields in ordinal order. Done when: a one-field change in a 50-field document gives
> one Replace, with a delta under a tenth of the record; null against absent gives Replace against
> Remove; 1.50m against 1.5m, and Int 5 against Long 5, each give a Replace; and equal documents give an
> empty delta.

**1.3 Arrays by position**
> Read docs/versioning-requirements-and-plan.md DL-3 and V-2. Diff arrays by aligning elements on their
> canonical bytes, with an O((N+M)·D) algorithm such as Myers', so memory stays linear. Between anchors:
> - a gap of equal length on both sides is diffed element by element, recursing;
> - an unequal gap becomes RemoveAt and Insert elements;
> - identical bytes removed at one position and inserted at another become one Move.
>
> Give every element indices that are valid at the moment it is applied, in list order, and write that
> rule in a comment where the indices are computed. Done when: a middle insertion gives one Insert; a
> removal gives one RemoveAt; a swap gives a Move; one field changed in the third object gives one
> Replace at [2].field; and 100 000 scalars with one insertion diff in milliseconds.

**1.4 Apply, invert, verify**
> Read docs/versioning-requirements-and-plan.md DL-4, DL-7 and V-3. Add DocumentDelta.ApplyTo and
> DocumentDelta.Invert.
> - ApplyTo walks the elements in order and checks each old value with CanonicalValue.Equal. At the
>   first mismatch it throws DeltaMismatchException, naming the path and — where the caller supplies one
>   — the version. It never returns a partially applied document.
> - Invert reverses the list and swaps old and new: Add ↔ Remove, Insert ↔ RemoveAt, and a Move becomes
>   the opposite Move.
>
> Add a property test over at least 10 000 generated pairs — three or more levels deep, arrays of
> objects, every implemented value type, nulls, absent fields — asserting that Compute(a, b).ApplyTo(a)
> equals b and that Compute(a, b).Invert().ApplyTo(b) equals a, canonically. Done when: both directions
> hold, a failure prints its seed, and a corrupted old value throws naming the path.

**1.5 Arrays matched by key**
> Read docs/versioning-requirements-and-plan.md DL-8, V-2, I-5 and R-9. Add diff options that name an
> element key for an array path. Where every element on both sides is an object with a distinct,
> non-null value at that key, match elements by key: a matched element that moved is a Move, a matched
> element that changed is diffed inside, and anything unmatched is an Insert or a RemoveAt. Otherwise, use
> positional matching. Record the matching used on the delta, and keep paths positional so ApplyTo is
> unchanged. Decide where a collection declares an element key — a new property on ColumnDescriptor is
> the expected answer, under D-4 — and record the decision against I-5 in §4.1. Done when: reordering
> three authors while one of them changes a field gives Moves and one Replace, with no whole-object
> removal; a duplicated key falls back to positions and says so; and step 1.4's property test also
> passes in keyed mode.

### Phase 2

**2.1 Identifiers and clocks**
> Read docs/versioning-requirements-and-plan.md HS-7, V-8 and §2.2 item 12. Make RecordHeader.ForNewRecord
> mint VersionId with RecordIdentity.Next(). Keep the greatest identifier issued as a high-water mark on
> the _collections descriptor, beside LastOwningCollectionId: every outermost commit that has pages to
> write and has moved the mark writes RecordIdentity's last value there before its journal frame, and
> open raises RecordIdentity to the stored mark, never lowering it. Add a clock abstraction that the
> engine reads the wall clock through, so a test can step it back, and a test hook that resets
> RecordIdentity, so a test can stand in for a new process. Fix the stale comments on RecordHeader and
> RecordFlags (§2.2, item 13). Done when: 10 000 identifiers minted in a tight loop ascend strictly; with
> a clock that steps back, they still ascend and each logical time is at least the recorded time; after a
> reset of RecordIdentity with the clock an hour behind and a reopen, the next identifier minted is above
> every identifier the database holds; a transaction that mints nothing dirties no page for the mark;
> AFreshVersionIdentifierIsMintedOnEveryWrite passes; and the engine suite passes.

**2.2 The policy in the catalogue**
> Read docs/versioning-requirements-and-plan.md HS-1, V-13 and §2.2 item 3. Make the collection's
> catalogue document the only source of truth for retention:
> - parse CollectionDescriptor.RetentionPolicy, reading an empty value as None;
> - add the keyframe interval and the large-delta ratio as new document fields;
> - add TokkDbConnection.SetRetentionPolicy(collection, policy, snapshotInterval = 8,
>   largeDeltaRatio = 0.5), running through ChangeSchema, and refusing reserved collections, an interval
>   below 1, and a ratio outside (0, 1];
> - make DbEntities.RetentionPolicy a read-only view of the catalogue;
> - remove RetireRow's RetentionPolicy parameter, and its refusal, from the method and from all seven
>   call sites: retirement behaves the same under every policy.
>
> Keep None as the default, and keep RemoveCurrentVersion refusing KeepVersions — that refusal is now
> the only one. Rewrite KeepVersionsIsDeclaredAndRefused to set the policy through the connection.
> Correct the comments §2.2 item 13 lists for this step. Done when: the settings survive a reopen; the
> fixture reads as None; the refusals are tests; no RetentionPolicy argument is passed anywhere outside
> the catalogue and SetRetentionPolicy; and every suite in the solution passes.

**2.3 The history collection and the contract**
> Read docs/versioning-requirements-and-plan.md HS-2, HS-3, V-4, V-14 and §3. When SetRetentionPolicy
> turns KeepVersions on, create a history collection named "_history:" followed by the collection's
> identifier, in the same transaction, and store its identity in HistoryCollectionId. Add internal
> creation and drop paths rather than weakening CollectionCatalog's refusal of reserved names, which
> covers dropping as well as creating. Add the dropHistory parameter to SetRetentionPolicy: a change
> back to None is refused while the collection has a history collection unless dropHistory is true, and
> with the flag the history is dropped and None set in one transaction (V-14). Step 7.4 adds the journal
> rule and the tests.
> DropCollection drops it too, in its transaction, by retiring every document in it and dropping its
> version index rather than by removing the catalogue entry alone — so that step 7.2's clearing reaches
> them. In TokkDb.Pages/Versions, add IVersionStore with the operations of §3.1, and a VersionStore that
> throws NotImplementedException for everything except CreateHistory and DropHistory. Route both the
> policy change and DropCollection through those two operations. Add an architecture test
> over the compiled assemblies for HS-3's two rules. Add a source-level test that lists every call site
> of RetireRow, UpdateRow, RewriteRow and FreeItem in TokkDb, TokkDb.Pages and TokkDb.Disk against a
> recorded list — one line per site saying why it cannot reach a user collection's records outside
> HS-3's four paths — and fails on any site the list does not name. Correct the comments §2.2 item 13
> lists for this step.
> Done when: an injected failure between the policy write and the creation leaves neither; history pages
> carry their own owning id; neither adapter lists the history collection; and both tests pass.

**2.4 Nodes, operations, and the index with a floor**
> Read docs/versioning-requirements-and-plan.md HS-4, HS-5, HS-6, HS-8, V-5 and V-6. Add VersionKind,
> VersionNode and Operation, with document mappings in the style of CollectionDescriptorDocument, and
> declare their columns for the history collection. A node document's RecordId is its version
> identifier; an operation document's is the operation identifier. Add VersionIndexRoot, whose root is a
> new field of the history collection's catalogue document. Add BPlusTree.Floor(key): one descent that
> remembers the nearest subtree to the left of the path, then at most one descent into that subtree's
> rightmost leaf. Give every operation document an entry keyed (operationId, operationId). Implement
> VersionStore's Node, Nodes, Floor and Operation over the index — never through FindLiveRow, which
> scans a reserved collection — plus the pointer check with its fallback to the index. Done when: every
> field round-trips; a property test compares Floor with a linear search; with 10 000 records of 100
> hand-written nodes each, Floor reads at most twice the tree's height, including when the answer is in
> the previous leaf; listing one record reads only its range; Operation(id) reads at most the tree's
> height; and a pointer to the wrong node falls back to the index.

**2.5 Attribution and operations**
> Read docs/versioning-requirements-and-plan.md HS-6, HS-9, V-7 and V-12. Add
> VersionAttribution(author, cause, comment) and TokkDbConnection.Attribute, returning an IDisposable
> scope that stamps every outermost transaction beginning while it is open, and refuse a nested scope.
> When the outermost transaction
> records its first version in a history collection, mint the operation identifier with
> RecordIdentity.Next() and write one operation document there, with the recorded time from the clock
> of step 2.1 and the scope's attribution. Test through a temporary recording hook, since record writes
> arrive in Phase 3. Done when: three recordings in one unit of work yield one operation; two units of
> work yield two; a rolled-back unit of work leaves none; and an operation outside any scope has an empty
> author and cause.

**2.6 Schema history**
> Read docs/versioning-requirements-and-plan.md HS-10, HS-4, WV-9 and V-11. Make SetColumns,
> CreateRelation and RemoveRelation record a schema node or a relation node through IVersionStore, in
> the transaction of the change, for every versioned collection they name. Turning versioning on records
> the current schema, one node per migration step still in the catalogue, marked as having unknown
> earlier declarations, and one relation node per relation currently naming the collection, marked as
> of unknown creation time. Read the schema and relation nodes of each history collection into memory
> at open and after each such change, so that nothing scans for them. Add IVersionStore.SchemaAt,
> returning the migration steps between two schema versions from schema history rather than from the
> catalogue. Done when: each change adds exactly one node; a rolled-back change adds none; on the
> fixture, turning versioning on records its pending rename and its relation; SchemaAt after a reopen
> reads no page; and after Rewrite clears the catalogue's log, SchemaAt still returns the rename.

### Phase 3

**3.1 The tests that mean None**
> Read docs/versioning-requirements-and-plan.md V-13 and R-8. Find every test in TokkDb.Tests,
> TokkDb.LLM.Storage.Tests and TokkDb.Assistant.Tests that asserts behaviour only None has: freed space
> reused, page counts unchanged after updates, no superseded images, file size after deletes. Among them
> are MutableRecordTests' FreedSpaceIsHandedOutAgain,
> ADeletedRecordIsGoneAndItsSpaceIsBackOnTheFreeList and AnUpdateDoesNotRewriteTheOldImageInPlace. Make
> each set None explicitly, with a one-line comment, and change no assertion. Done when: the list is in
> the message and all three suites pass.

**3.2 Insert and update**
> Read docs/versioning-requirements-and-plan.md WV-1, WV-2, WV-5, WV-6, HS-1, HS-7, HS-8, V-1, V-3, V-7
> and V-8. Under KeepVersions, route DbEntities.RemoveCurrentVersion and WriteImage through
> IVersionStore.RecordInsert and RecordSupersede. Remove the refusal in RemoveCurrentVersion; retirement
> still frees the data slot exactly as today. Correct the comments §2.2 item 13 lists for this step — on
> RecordHeader.PreviousVersion, RetentionPolicy.KeepVersions, DbEntities.RetentionPolicy and
> RemoveCurrentVersion.
> - Insert writes an Insert node (no delta, distance 0), then the live image pointing at it.
> - Update follows WV-2's five steps in order, including both keyframe rules, with "reach or exceed k".
>   A keyframe of either kind stores no delta. A keyframe copy rewrites the head's node with its image
>   and upserts its index entry, unless the node already holds an image, which is kept.
> - A new version's identifier is the greater of RecordIdentity.Next() and the successor of the record's
>   latest version identifier, and the new image's header carries the same VersionId.
>
> Add DbEntities.HeadVersion. Delete KeepVersionsIsDeclaredAndRefused, whose refusal this step removes.
> Leave Delete for step 3.3. Done when:
> - 10 000 inserts add 10 000 nodes, one operation and no image;
> - a first update stores the root image and one delta;
> - an update that changes nothing adds no node;
> - with the ratio at 0.5, a complete rewrite stores an image rather than a delta;
> - the data chain's count and free space match None;
> - a scan reads no history page;
> - for k = 1, 4, 8 and 32 over 200 updates, distances stay below k, every non-head distance-0 node has
>   an image, and no distance-0 node has a delta;
> - lowering k from 32 to 4 partway through rewrites no node and keeps both bounds;
> - after a reopen in a new process with the test clock set an hour before the last write, the next
>   update's identifier is greater than the head's.

**3.3 Delete and baseline**
> Read docs/versioning-requirements-and-plan.md WV-3, WV-4, V-9 and G-9. Under KeepVersions, Delete copies
> the head's image into its node if that node is a keyframe, retires the image (leaving every index, as
> today), and writes a Delete tombstone whose parent is the head. Superseding a head that has no node
> first writes a Baseline node, identified by the header's VersionId and flagged as having no recorded
> origin, with its image copied. Done when: a deleted record is absent from GetById, GetAll, queries and
> every index, and the count falls; and on the fixture, switched on with one record updated, that record
> has Baseline and Update, the others have nothing, and every page holding only untouched records is
> byte-identical before and after.

**3.4 Atomicity, invariants, and Rewrite**
> Read docs/versioning-requirements-and-plan.md WV-4, WV-7, WV-8 and WV-10 and §2.2 items 4 and 5. Implement
> IVersionStore.Verify for WV-10's four invariants. Use FaultInjectingDiskManager to fail at every write
> of an insert, an update with a keyframe copy, a delete and a schema change, in turn. After each
> failure, reopen and assert either the state before or the state after, and that Verify passes.
>
> Make Rewrite keep VersionId and PreviousVersion and create no version. Before it migrates a head of a
> versioned collection, have it ask the schema mapping whether anything in that head's stored image would
> be Unmapped (a removed column's value, a lossy retype). If so, store that image in the head's node
> first — writing the head's Baseline node (WV-4) when it has none, and pointing the header at it. The
> mapping arrives in step 4.4, so until then treat every Remove or Retype step as losing something. Show
> by test that the in-place paths of SystemDocumentStore.Write and MigrateRow cannot reach a versioned
> record's content other than through this rule.
>
> Done when: every fault point leaves a consistent state; Rewrite on a versioned collection leaves version
> lists and heads unchanged and moves no pointer except from zero to a new Baseline; after a removal
> followed by Rewrite, the head's node holds the pre-rewrite image with the removed value; and a record
> with no node gains a Baseline holding it.

### Phase 4

**4.1 Reconstruction**
> Read docs/versioning-requirements-and-plan.md RH-5, RH-7, RH-8, WV-5 and V-3. Implement
> IVersionStore.Reconstruct:
> - the head reads its live image;
> - any other version walks parents to the nearest node holding an image, then for each node on the way
>   down migrates the image to that node's schema version, using migration steps from SchemaAt (never
>   the catalogue), and applies the delta with ApplyTo, passing the version.
>
> Hold a CatalogLock read lease throughout, received the way QueryService receives it. Return a
> ReconstructionReport with versions examined, keyframe used, deltas applied, pages read and time taken.
> Done when:
> - for k = 1, 4, 8 and 32, over 200 updates including complete rewrites, plus a hand-built second
>   branch, no report exceeds k − 1 deltas;
> - a history interleaving a rename, a retype and a removal reconstructs every version to what was
>   captured at write time, including after Rewrite;
> - a SetColumns started mid-reconstruction waits.

**4.2 The forest and a version**
> Read docs/versioning-requirements-and-plan.md RH-1, RH-2, V-9 and V-10. Add DbEntities.History,
> returning the version forest. Each node comes with parent, kind and schema version, and with its
> operation's recorded time, author, cause and comment. The result names the head, the leaves and every
> root's cutFrom, and flags an operation recorded earlier than its predecessor. Add GetAsOf(recordId,
> versionId), returning the value through the current schema with its Unmapped list — for now, map only
> renames, and leave the full rules to step 4.4 — and GetStoredAsOf, returning the stored document. Done
> when: History of three writes in one unit of work inside one scope shows one operation with the
> scope's attribution; each
> version equals what GetById returned right after its write; and GetStoredAsOf on a version from before
> a rename shows the old name.

**4.3 As of a moment, and the schema as of a moment**
> Read docs/versioning-requirements-and-plan.md RH-3, RH-9, V-5 and V-8, and N-9 and N-10. Add
> GetAsOf(recordId, moment): one IVersionStore.Floor on (recordId, the largest identifier whose timestamp
> is at or before the moment), and then the node found. It returns the version, or one of four
> outcomes: not yet created, deleted, before recorded history, or no such record. Add
> TokkDbConnection.SchemaAsOf over schema and relation nodes. Done when:
> - the four absences are four outcomes;
> - for a record with 1 000 versions, the report shows at most twice the tree's height in pages;
> - with the test clock stepping back during writes, answers stay monotone in the moment and History
>   flags the step;
> - after a reopen in a new process with the clock set behind the last write, a new version is found by
>   GetAsOf at its own logical time, and the previous head at any earlier moment, and a schema node
>   written after that reopen sorts after the one before it, so SchemaAsOf stays monotone;
> - SchemaAsOf returns what was declared at each moment between a column added, a rename and a relation
>   created.

**4.4 Diff and the mapping rules**
> Read docs/versioning-requirements-and-plan.md RH-4, RH-6, V-10, N-13 and G-6. Add a schema mapping over
> the migration steps SchemaAt returns, implementing each row of V-10's table exactly. A retype is
> lossless for a value when converting it back gives the same stored bytes. Values and elements that
> cannot be mapped go to Unmapped with a reason, the column and the original; they are never dropped.
> Apply the mapping in GetAsOf. Add DbEntities.Diff(recordId, from, to, asStored = false): when to's
> parent is from and to has a delta, map that delta; otherwise diff the two reconstructions. asStored
> skips mapping. Apply the rules step by step, oldest first, as V-10 states, and report a value at the
> first step that cannot carry it. Replace step 3.4's conservative check in Rewrite with this mapping.
> Done when: each row of V-10's table is a test, including a removal followed by a new column of the
> same name; a rename, then a lossless retype, then a lossy
> retype reports one Unmapped naming the third step; a parent-and-child diff reads no image; a diff across
> a rename shows no Remove/Add pair; a lossy retype reports unmapped values instead of converting them; a
> diff between two leaves equals Compute over their reconstructions; and Rewrite stores an image only for
> heads that have something unmapped.

### Phase 5

**5.1 Measure**
> Read docs/versioning-requirements-and-plan.md NF-2 to NF-5, V-1, V-13, I-1, I-2 and F-12, and risks R-1
> and R-11. Add benchmarks to TokkDb.Benchmarks, registered in Program.cs and reported through Measurement:
> - history size and reconstruction time for k = 1, 2, 4, 8, 16 and 32, and the large-delta ratio at 0.25,
>   0.5, 0.75 and off, each over four workloads: synthetic small edits to 50-field records, appends to an
>   array and complete rewrites; the benchmark project's Publication documents with their nested author
>   and keyword arrays; an assistant-like history of an import followed by scattered corrections; and a
>   wide-record edit history;
> - history size against versions, from 1 to 100;
> - write amplification under None and KeepVersions, with 0 and 3 secondary indexes, over an
>   assistant-like import of 10 000 rows of 12 columns in one unit of work, small edits to wide records,
>   and a mix with deletes. Report pages dirtied, journal bytes, file growth, and durable and bulk
>   latency;
> - DatabaseOpenBenchmark with 500 versioned collections.
>
> Run with --phase "Versioning — measurement". Then write, in docs/benchmarks.md, a proposal for I-1 and
> I-2 with the numbers that justify each. If a workload shows reconstruction cost unbounded in bytes under
> both rules, say so against F-12 rather than adding a rule. Name the workload each default was chosen
> for, and give what the same default costs on the others, so a collection with a different workload knows what to
> override. Add a plain statement of what KeepVersions by default costs. Do not change any default. Done when: every number is reproducible by the command at the top of
> docs/benchmarks.md, and the message ends with the proposal and the question for I-4: "flip the default
> — yes or no?"

**5.2 The default**
> Only after an explicit yes to step 5.1's question. Read docs/versioning-requirements-and-plan.md V-13,
> I-1, I-2, I-4 and R-8. Record the accepted values for I-1 and I-2 in §4.1, with a link to the benchmark
> run. If the answer was yes, make KeepVersions, with the accepted interval and ratio, the default for user
> collections created through CreateCollection and CreateDatabase, creating each history collection in
> the same transaction. Correct every comment that states the default. Run the engine, LLM storage and
> assistant suites: each failure is either a
> None test step 3.1 missed — give it None and list it — or a real defect, fixed here. If the answer was
> no, record in §4.1 that the default stays None, and why. Done when: §4.1 records I-1, I-2 and I-4 with
> evidence, and all three suites pass under the chosen default.

### Phase 6

**6.1 Restore and branches**
> Read docs/versioning-requirements-and-plan.md RB-1, RB-3, RB-4, V-3, V-7, V-9, G-4 and G-5. Add
> DbEntities.Restore(recordId, versionId): reconstruct and map the version, then write it through the
> seam as IVersionStore.RecordSupersede, with kind Restore, parent set to the restored version, and the
> replaced head recorded. Compute the Restore node's delta from the restored version as reconstruction
> presents it at the current schema version — Reconstruct(v) migrated forward through SchemaAt — to the
> document written, never from the replaced head, and write the node even when that delta is empty
> (V-7). The replaced head becomes a leaf, and copies its image into its node if it is a keyframe. Return
> the Unmapped list. Refuse a tombstone or the current head as the target; unique and relation refusals
> come from the ordinary write path. Every refusal changes nothing. Done when: restoring v2 of v1–v4 and
> then editing leaves leaves {v4, the edit}, both readable, with WV-5's bound on both; every Restore
> node reconstructs through V-3 to what GetById returned right after the restore, including one made
> after a lossy retype; a value in a since-removed column comes back as Unmapped; a unique refusal
> names column and holder and leaves head, history and indexes unchanged; restoring the head is
> refused; and every version readable before a restore reads the same after it.

**6.2 Restoring the deleted, and the §2.4 scenario**
> Read docs/versioning-requirements-and-plan.md RB-2, RB-3 and S-1. Allow Restore on a record whose head
> is a tombstone, bringing it back under its identity and into every index. Refuse a version the index no
> longer holds, with a typed error. Write S-1 end to end inside an attribution scope. Done when: S-1
> passes with the history, the GetAsOf results and the diff it states; the restored record keeps its
> Ulid and is found through every index; the refusal of a version the index no longer holds is a test;
> and N-5 is a test. N-3 becomes a test at step 7.1, once a purge exists.

### Phase 7

**7.1 Purge**
> Read docs/versioning-requirements-and-plan.md RP-1, RP-2, RP-3, V-15, I-7, G-7 and S-6. Implement V-15's
> four steps as IVersionStore.Reroot and Remove — Reroot gives the node its image, removes its delta and
> records cutFrom — and on top of them add
> TokkDbConnection.PurgeHistory(collection, before) and DbEntities.PurgeHistory(recordId, before). Run
> one record per transaction, in batches (choose the default and record it against I-7), safe to resume,
> and end the collection-wide purge with one pass that removes every operation document no remaining
> node references; the per-record purge removes none. Before committing each record, check its kept
> forest — WV-10's invariants, and every kept node's parent is kept, or the node is a root holding an
> image or the live head — and roll that record back and report it if the check fails. Put V-15's proof
> in a comment where the kept set is computed, because it names what the tests must cover. Done when:
> - a test hook that skips one rerooting image makes the purge roll that record back, report it, and
>   leave its history unchanged;
> - over generated histories with branches, restores of versions older than the moment, deletes and
>   schema changes, every sampled t ≥ before answers GetAsOf identically before and after;
> - every kept version reads the same and reaches an image within its stored distance;
> - S-6 returns a forest with cutFrom on each root, and no rerooted node has a delta;
> - a purge interrupted by the fault injector and run again gives the same result as one run;
> - purging one record leaves every other record byte-identical;
> - after a collection-wide purge no operation document is left that no node references, and after a
>   per-record purge every operation document is still there;
> - N-3: a restore to a purged version is refused as no longer kept.
>
> The file-search half of RP-2 needs steps 7.2 and 7.3, and is added there.

**7.2 Secure release**
> Read docs/versioning-requirements-and-plan.md RP-4, V-17, §2.2 item 8 and R-10. In the page layer, make
> every release clear what it releases:
> - BaseItemsPage.FreeItem clears the slot's bytes;
> - UpdateRow clears the rest of the slot beyond the bytes it writes, which covers
>   SystemDocumentStore.Write, MigrateRow and the catalogue's own saves;
> - Compact clears the run it vacates;
> - freeing an overflow page clears its payload;
> - index leaf and interior pages clear removed entries, and the space a split or merge leaves behind;
> - IndexCatalog.Drop clears every node page before recording it as retired, which covers DropIndex,
>   SetColumns rebuilds and DropAll;
> - DropCollection clears its collection's primary index pages.
>
> This applies whatever the retention policy, and nothing in it knows about versions. Done when:
> - after updates, deletes and compaction under None, a search of the file finds no retired value;
> - after a system document is rewritten in place with a shorter one, the rest of its slot reads back
>   cleared;
> - after DropIndex, every page the index occupied reads back cleared apart from its header;
> - after DropCollection on a versioned collection with a secondary index and some history, a search of
>   the database file finds none of its values (step 7.3 adds the journal half);
> - every suite in the solution passes, and a quick measurement of the cost is in the message, for NF-5.

**7.3 Erase**
> Read docs/versioning-requirements-and-plan.md RP-5, V-17, G-8 and §2.2 item 11. Implement
> IVersionStore.EraseRecord and DbEntities.Erase(recordId): remove the live image if there is one, every
> node of the record including the tombstone, and every index entry — in one transaction, leaving
> operation documents to the collection's next purge (RP-5). Mark that transaction, or the outermost one
> it joins,
> as having erased something; mark transactions running DropCollection, every transaction of a purge —
> collection-wide or per record, its final operation pass included — and DropHistory the same way. Make
> PageManager's commit discard such a transaction's journal frame as soon
> as its commit record is durable, and leave every other commit, DropIndex included, exactly as it is
> today. Done when:
> - right after a purge commits, a distinctive value that existed only in removed versions is in neither
>   file;
> - a record carrying a distinctive string in a live field, in three past versions and in a secondary
>   index key is erased;
> - right after the commit — connection still open, no further transaction — neither the database file
>   nor its journal contains the string;
> - an erase inside a larger unit of work discards that unit's frame at its commit;
> - right after DropCollection commits on a versioned collection, neither file contains any of its values;
> - an ordinary commit still leaves its frame, and recovery on the next open behaves as before;
> - GetAsOf reports no such record;
> - V-17's list of what is outside the boundary, with its two stated exceptions, is in the XML
>   documentation of Erase.

**7.4 Turning versioning off, the report, and verification**
> Read docs/versioning-requirements-and-plan.md RP-6, RP-7, RP-8, V-14 and N-1, N-2 and N-6.
> SetRetentionPolicy already refuses KeepVersions to None without dropHistory (step 2.3): make the
> refusal name the flag, and mark the dropHistory transaction for V-17's journal rule. Add
> TokkDbConnection.HistoryReport and VerifyHistory. VerifyHistory reports the first node that breaks
> WV-10's invariants, fails DL-7's check on reconstruction, or has a stored distance smaller than its
> actual one. Done when:
> - N-1, N-2 and N-6 are tests;
> - turning versioning off with dropHistory removes the history collection in one transaction, a failure
>   injected in the middle leaves policy and history as they were, and right after the commit a value that
>   existed only in past versions is in neither file;
> - turning versioning back on gives a record a Baseline at its next change;
> - the report agrees with a scan;
> - each kind of deliberate damage is reported at the right node.

### Phase 8

**8.1 The model**
> Read docs/versioning-requirements-and-plan.md NF-6, WV-10 and V-15. Write a model-based test that
> generates seeded sequences of at least 1 000 operations over at least 50 records: inserts, nested and
> array updates (some keyed), deletes, restores to random versions, purges (per collection and per
> record), renames, retypes, removals and Rewrite. Keep an in-memory model of every version's document,
> parent, kind and operation. After every operation, assert every version, the forest shape, WV-10's
> invariants and V-15's invariant. Repeat with 100 random kill points, asserting history against the
> model at the last committed operation. Done when: both pass, a failure prints its seed, and the time
> both take is stated.

**8.2 The guarantees**
> Read docs/versioning-requirements-and-plan.md §6 and G-1 to G-9. Write VersioningGuaranteeTests: one
> test per guarantee, through the public surface of §3.2 only — no node, page or index inspection, which
> a reflection-based check over the test class enforces. Each test is phrased as the promise it checks.
> Where a guarantee spans many cases (G-6, G-7), drive it from generated data. Done when: G-1 to G-9 pass,
> and deliberately breaking the corresponding requirement — shown in the message by reverting one line
> for each of three guarantees — fails the guarantee test and not only a mechanism test.

**8.3 Compatibility, the full run, and traceability**
> Read docs/versioning-requirements-and-plan.md NF-1, NF-7 and §11. Add the NF-7 test on the step 0.2
> fixture: open, read every record, switch on, update, and assert RootPage.CurrentFormatVersion is 2. Run
> the benchmarks with --records 100000 and --phase "Versioning". Write a paragraph in docs/benchmarks.md
> reading the numbers against NFR-2, NFR-4 and VR-6, including where deltas lose. Run every S and N
> scenario of §7 that is not already a test — S-9 and N-11 wait for step 6.3 — and fill §11's table with
> the covering test for every requirement whose step has run; step 6.3 fills the row for VR-9 when it
> runs. Then audit what the plan changed. Search TokkDb.Pages, TokkDb.Documents and TokkDb for
> comments that still describe the old behaviour — "this pass", "D-5", "reserved", "not implemented",
> "never read", "only Live" — and correct each one. In docs/requirements-and-plan.md, update the audit
> row that says versioning has no implementation, gap-analysis row 3, and the risk about D-5 slipping,
> to describe what landed. Done when: every scenario but S-9 and N-11 is a passing test; every requirement
> names a test — or a benchmark, for the measurement requirements only, or step 6.3, for RB-5 alone; the
> comment search finds nothing stale; and the message lists every comment and document corrected.

### Step 6.3, deferred until after Phase 8

Related restore is off the critical path (§9): VR-9 is a should, and nothing in Phases 7 to 9 depends on
it. It runs after step 8.3; Phase 9 may run before or after it. It keeps its number, because §11 and I-6
refer to it.

**6.3 Related restore**
> Read docs/versioning-requirements-and-plan.md RB-5, V-16, I-6, S-9 and N-11. Add
> DbEntities.RestoreAsOf(recordId, moment, followRelations). Restore the record to its version at the
> moment. Then, for each relation that SchemaAsOf says existed at the moment with a restored collection as
> its source:
> - take the source column's value at the moment;
> - find the holder through the target column's current index;
> - accept the holder only if its version at the moment held the same value, and restore it as of the
>   moment.
>
> Keep a visited set, cap the set's size (choose the default and record it against I-6), and do
> everything in one transaction. Refuse an unverifiable holder, including one deleted since, naming the
> relation and the value. Never change a record that refers to a restored one. Done when: S-9 passes; a
> relation created after the moment is not followed; N-11 is refused before anything is written; a cycle
> terminates; exceeding the cap fails with the cap and the size reached; and §11's row for VR-9 names
> the covering tests.

### Phase 9

**9.1 Version references in the journal**
> Read docs/versioning-requirements-and-plan.md AJ-1 and V-18, and docs/assistant-requirements-and-plan.md
> D-17, TR-2 and TR-2b. Add PreviousVersionId and VersionId to DataChange. Change DataChanges.Insert,
> Update and Delete to take those two identifiers instead of field values: remove their content hashes
> and field payloads, and keep the request, record, collection, time, disposition and reversibility.
> Keep DataChanges.Structural, JournalValue, FieldChange and PayloadLimits for structural changes only,
> and say so in their XML documentation. Persist the new fields in EngineTraces' _dataChanges document,
> reading an old document's missing versions as null. Update ChangeJournalTests: the record-change tests
> now assert fixed-size references, and the structural tests are unchanged. Done when: importing 10 000
> records produces change records whose size does not depend on the rows, an old _dataChanges document
> still reads, and the assistant suite passes.

**9.2 Version operations on both storages**
> Read docs/versioning-requirements-and-plan.md AJ-2, AJ-3 and R-5. Add HeadVersion, Keeps, DiffVersions,
> RestoreVersion, PurgeRecordHistory and Erase to TokkDb.Assistant.Storage's IStorage, documented in its
> style. DiffVersions returns column-by-column changes, old beside new. Implement them on TokkDbStorage
> over DbEntities, mapping engine refusals to the contract's own errors, and on MemoryStorage by keeping
> one full copy per version, with identifiers from the same monotonic source. Make every collection
> TokkDbStorage creates KeepVersions, and switch any user collection still at None on at open. Add
> contract tests running against both backends. Done when: the contract suite passes on both;
> HeadVersion after an update returns the version the update produced, and after a delete the tombstone;
> a unique refusal surfaces as DuplicateValue; a None collection is switched on at open without a rewrite;
> and the contract project still references nothing.

**9.3 Compensation and reversibility**
> Read docs/versioning-requirements-and-plan.md AJ-4, AJ-5, AJ-8, V-7, V-18 and N-14, and
> docs/assistant-requirements-and-plan.md AG-11 to AG-11f and step 4.7. Compute a record change's
> reversibility before it runs: Reversible when its collection has no unique column and takes part in no
> relation, ReversibleWithConditions otherwise. Implement compensation of one request as AJ-4's two
> passes, inside one unit of work.
> - The validation pass writes nothing and collects every blocker: records touched since, and end-state
>   conflicts computed from current values and DiffVersions — unique collisions with records the request
>   did not change, references that would not resolve, and deletions still referenced from outside.
>   Refuse the whole undo naming all of them. Make the pass take any subset of the request's changes,
>   treating records left out as unchanged by the request, so the assistant's partial offer (its AG-11b)
>   is validated by the same code.
> - Only then, the replay: every change in exact reverse order, without re-checking the compensation's
>   own versions, aborting atomically on any constraint refusal.
>
> Where TokkDb.Assistant.Agents exists, put it there; otherwise write it as a test-local helper over
> IStorage, so step 4.7 of the assistant plan can adopt it unchanged. Add AJ-8's check wherever the
> assistant's retention settings live. Done when:
> - a request that updated a record twice is undone;
> - X a→b, Y c→a, X b→c on a unique column is undone;
> - an A→B→A edit blocks the undo;
> - an undo blocked by one touched record and one unique collision is refused before any write, naming
>   both;
> - the intermediate-state case of AJ-4 is refused atomically, naming the record;
> - validating a subset that leaves out the record whose restore would have released a unique value
>   reports the collision this creates for another record in the subset;
> - the three counterexamples of V-18 refuse whole and name the blocker;
> - the same tests pass against MemoryStorage;
> - a configuration purging inside the window is refused.

**9.4 The trace, erasure, and G-10**
> Read docs/versioning-requirements-and-plan.md AJ-6, AJ-7, G-10, S-7, S-8 and S-10. Build the detail
> panel's before-and-after data for a record change from DiffVersions, showing "the values of this change
> are no longer kept" once its versions are purged. Make erasure through the assistant run the engine's
> erase and clear step input and output payloads of every request whose DataChange names the record, in
> one unit of work, with the confirmation text stating that conversations are kept. Write
> UndoGuaranteeTests for G-10 against both backends, through IStorage only, and S-7, S-8 and S-10 as tests.
> Done when: G-10, S-7, S-8 and S-10 pass, and a search of the file after S-7 finds the string only in the
> user's own conversation entry.

---

## 11. Traceability

How each versioning item of the other two plans is closed. Step 8.3 filled in the tests column for every
step that had run; the assistant rows are filled by their steps in Phase 9, and VR-9 by step 6.3. Every
test named is in `TokkDb.Tests` unless said otherwise.

| Plan | Item | Closed by | Steps | Tests |
|---|---|---|---|---|
| Engine | D-5, the open question | V-1 | 0.1 | — |
| Engine | VR-1 | HS-1, WV-1, V-13 | 2.2, 3.2, 5.2 | `RetentionPolicyTests`, `HistoryCollectionTests`, `VersionRecordingTests` |
| Engine | VR-2 | DL-1, DL-2, WV-2 | 1.1, 1.2, 3.2 | `Delta/DocumentDiffTests`, `Delta/DeltaPathTests`, `Delta/DeltaApplyTests`, `VersionRecordingTests` |
| Engine | VR-3 | DL-3, DL-4, DL-8 | 1.3–1.5 | `Delta/ArrayDiffTests`, `Delta/KeyedArrayDiffTests`, `Delta/DeltaPropertyTests`, `Delta/DeltaStorageTests` |
| Engine | VR-4 | HS-5, HS-6, HS-9, RH-1 | 2.4, 2.5, 4.2 | `VersionStoreTests`, `AttributionTests`, `HistoryReadTests` |
| Engine | VR-5 | RB-1, V-9, G-5 | 6.1 | `RestoreTests`, `RestoreDeletedTests`, `VersioningGuaranteeTests` (G-4, G-5), `ScenarioTests` (S-2) |
| Engine | VR-6 | V-1, WV-5, NF-3 | 3.2, 4.1, 5.1 | `VersionRecordingTests`, `ReconstructionTests`, `ScenarioTests` (S-5); `HistorySizeBenchmark` |
| Engine | VR-7 | RH-2, RH-3, RH-4 | 4.2–4.4 | `HistoryReadTests`, `AsOfTests`, `VersionDiffTests`, `ScenarioTests` (S-3) |
| Engine | VR-8 | WV-3, RB-2 | 3.3, 6.2 | `VersionDeleteTests`, `RestoreDeletedTests` |
| Engine | VR-9 | RB-5, V-16 | 6.3, after Phase 8 | `RelatedRestoreTests` (S-9, N-11, the cycle, the cap, an unfollowed later relation, an untouched referrer) |
| Engine | VR-10 | HS-4, RP-7, NF-4 | 2.4, 5.1, 7.4 | `VersionStoreTests`, `HistoryVerificationTests`; `HistorySizeBenchmark` |
| Engine | VR-11 | HS-7, HS-8, V-6 | 2.1, 2.4, 3.2 | `IdentifierTests`, `VersionStoreTests`, `VersionRecordingTests` |
| Engine | VR-12 | WV-2, WV-4, WV-8, V-1 | 3.2–3.4 | `VersionRecordingTests`, `VersionDeleteTests`, `VersionRewriteTests`, `MutableRecordTests`, `VersionAtomicityTests` |
| Engine | VR-13 | WV-6 | 3.2 | `VersionRecordingTests` |
| Engine | NFR-2, reconstruction under 50 ms | NF-1 | 8.3 | `HistorySizeBenchmark` (`docs/benchmarks.md`, 2026-09-14) |
| Engine | NFR-4 | NF-2 | 5.1, 8.3 | `HistorySizeBenchmark`, `WriteAmplificationBenchmark` |
| Engine | NFR-5 | DL-7, RP-8 | 1.4, 7.4 | `Delta/DeltaApplyTests`, `HistoryVerificationTests` |
| Engine | NFR-8 | NF-6, §6 | 8.1, 8.2 | `Model/VersioningModelTests`, `VersioningGuaranteeTests` |
| Engine | UI-1 | F-10 | — | — |
| Assistant | D-17, "full undo waits for versioning" | V-18, AJ-4, AJ-5 | 9.3 | `CompensationTests` and `UndoGuaranteeTests` (both backends), in `TokkDb.Assistant.Tests`; the compensation itself is the test-local `Compensation` there, for step 4.7 of the assistant plan to adopt |
| Assistant | SC-12, the six version operations | AJ-2 | 9.2 | `StorageContractTests.Versions` (both backends), `ContractShapeTests` |
| Assistant | SC-12a, every assistant collection versioned | AJ-3 | 9.2 | `TokkDbStorageTests.Every_collection_the_assistant_writes_to_keeps_versions` |
| Assistant | TR-2b, for record changes | AJ-1 | 9.1 | `ChangeJournalTests`, `TraceStoreTests` (an old document reads with null versions) |
| Assistant | TR-6, the before-and-after table | AJ-6 | 9.4 | `AssistantErasureAndTraceTests.The_before_and_after_table_comes_from_the_versions_and_says_when_they_are_gone` |
| Assistant | TR-8, no purge inside the compensation window | AJ-8 | 9.3 | `CompensationTests.A_configuration_that_purges_inside_the_compensation_window_is_refused` (`RetentionWindows`) |
| Assistant | AG-11a to AG-11f, compensation | AJ-4 | 9.3 | `CompensationTests` (both backends) |
| Assistant | NF-4d, NF-4d1 | V-17, RP-4, AJ-7 | 7.2, 9.4 | `SecureReleaseTests`, `EraseTests` (7.2, 7.3); `AssistantErasureAndTraceTests.S7_erase_through_the_assistant` (9.4) |

The requirements of §5 by group, and the tests that cover each (step 8.3):

| Group | Tests |
|---|---|
| DL-1 to DL-8 | `Delta/DocumentDiffTests`, `Delta/DeltaPathTests`, `Delta/DeltaApplyTests`, `Delta/ArrayDiffTests`, `Delta/KeyedArrayDiffTests`, `Delta/DeltaPropertyTests`, `Delta/DeltaStorageTests` |
| HS-1 to HS-10 | `RetentionPolicyTests`, `HistoryCollectionTests`, `Architecture/RetirementCallSiteTests`, `Architecture/VersionStoreArchitectureTests`, `VersionStoreTests`, `IdentifierTests`, `AttributionTests`, `SchemaHistoryTests`, `CollectionCatalogTests` |
| WV-1 to WV-10 | `VersionRecordingTests`, `VersionDeleteTests`, `VersionRewriteTests`, `VersionAtomicityTests`, `SchemaHistoryTests`, `ReconstructionTests` |
| RH-1 to RH-9 | `HistoryReadTests`, `ReconstructionTests`, `SchemaMappingTests`, `AsOfTests`, `VersionDiffTests` |
| RB-1 to RB-4 | `RestoreTests`, `RestoreDeletedTests` |
| RB-5 | `RelatedRestoreTests` |
| RP-1 to RP-3 | `PurgeTests` |
| RP-4 | `SecureReleaseTests`, `ItemsPageTests` |
| RP-5 | `EraseTests` |
| RP-6 | `SwitchOffTests`, `HistoryCollectionTests` |
| RP-7, RP-8 | `HistoryVerificationTests` |
| NF-1 to NF-5 | `docs/benchmarks.md` |
| NF-6 | `Model/VersioningModelTests` |
| NF-7 | `CompatibilityTests` (in `PreVersioningFixtureTests.cs`), `PreVersioningFixtureTests` |
| G-1 to G-9 | `VersioningGuaranteeTests` |
| G-10, AJ-1 to AJ-8 | `UndoGuaranteeTests`, `CompensationTests`, `StorageContractTests.Versions`, `ChangeJournalTests`, `AssistantErasureAndTraceTests`, in `TokkDb.Assistant.Tests` |

The scenarios of §7, and where each is a test: S-1 `RestoreDeletedTests`; S-2, S-3 and S-5 `ScenarioTests`; S-4
`VersionDeleteTests`; S-6 `PurgeTests`; S-7, S-8 and S-10 `UndoGuaranteeTests` and `AssistantErasureAndTraceTests` in `TokkDb.Assistant.Tests`; S-9 `RelatedRestoreTests`. N-1 `RetentionPolicyTests`; N-2 `HistoryCollectionTests`; N-3 `PurgeTests`; N-4, N-5 and N-12
`RestoreTests`; N-6 `HistoryVerificationTests`; N-7 `VersionAtomicityTests`; N-8 `SchemaHistoryTests` and
`VersionRewriteTests`; N-9 `AsOfTests` and `EraseTests`; N-10 `AsOfTests`; N-11 `RelatedRestoreTests`; N-13
`VersionDiffTests`; N-14 `CompensationTests`.

---

## Appendix: what changed in draft 6

A final review before Phase 0, read against the code of the working tree as well as against the text.
Twenty points: three would have led to a wrong implementation or an unfair measurement, four were gaps a
careful implementer would have met, and thirteen were places where sections or steps did not agree.
None changes a settled decision.

**A restore node had no delta base, and V-7 would have suppressed it (point 1).** V-1 makes a node's
delta relative to its parent, V-9 makes the restored version the parent, and §3 and step 6.1 recorded a
restore "like any other write", whose delta WV-2 computes from the head. Either reading breaks: a delta
from the replaced head fails DL-7 on every read of the restore, and a delta from the parent is usually
empty, which V-7 said creates nothing. **V-7** now states the base — the parent as reconstruction
presents it at the current schema version — and that a restore always writes its node; **RB-1**, **RB-3**
(the current head is refused as a target) and step 6.1 follow.

**The full-copy baseline stored a delta too (point 2).** A count-rule keyframe kept its image and its
delta, so at *k* = 1 every version stored both, and NF-2's baseline was larger than full copy by one delta
per version. **V-1** now says a keyframe of any kind stores no delta; HS-5, WV-2, WV-5 and RH-4 follow.

**An in-place rewrite left the old bytes beyond the new record (point 3).** `UpdateRow` writes the new
bytes over the old and leaves the rest of the slot; the assistant's step payloads are rewritten that way,
so AJ-7's erase would have left the value in the file. **RP-4**, **V-17** and step 7.2 now clear the tail
of a slot after an in-place rewrite, and §2.2 item 8 records the fact.

**Logical time was ordered per record only (point 4).** Schema nodes, relation nodes and operations are
ordered by logical time as well, and a restart with the clock behind would have put a new one before its
predecessor, so `SchemaAsOf` and a related restore could answer wrongly. **V-8** and **HS-7** now seed
`RecordIdentity` from a high-water mark on the `_collections` descriptor, written by every outermost
commit that moves it; the page is nearly always dirty already. Step 2.1 builds it, and N-10 and R-7 say so.

**Nothing said how an operation or a schema node is found (points 5 and 12).** A history collection has
the reserved prefix and so no primary index; `FindLiveRow` on it is a scan, which would have broken RH-3's
page bound and NF-1. **V-5** and **HS-4** now give every operation document an entry in the version index
and hold schema and relation nodes in memory; HS-6 and steps 2.4 and 2.6 follow. The reverse question —
which operation documents nothing references — is answered once, in a final pass of a collection-wide
purge; a per-record purge and an erase leave operation documents alone, because they hold no record
values (**V-15**, **RP-1**, **RP-3**, **RP-5**, steps 7.1 and 7.3).

**Turning versioning on recorded no existing relations (point 6).** **V-11**, **HS-10** and step 2.6 now
record a relation node for every relation naming the collection at switch-on.

**`Rewrite` could still lose values from a record with no node (point 7).** WV-8 stored the pre-migration
image in the head's node, and a record switched on but never changed has none. **WV-8**, **WV-4** and step
3.4 now write the `Baseline` first, which is also the one case in which `Rewrite` moves a pointer.

**Steps and exits that did not agree (points 8 to 11, 13, 14 and 20).** Step 2.3 adds the internal drop
path that the catalogue's refusal of reserved names needs, and implements V-14's refusal at once rather
than leaving four phases in which a change to `None` would have dropped a history silently; step 7.4
keeps the journal rule and the tests. `KeepVersionsIsDeclaredAndRefused` is deleted at step 3.2, where
its refusal goes, not at 5.2. Phase exits say where a clause that needs a later phase is checked, and N-3
moves to Phase 7. HS-9 and step 4.2 say "in one unit of work inside one scope", because V-7 makes an
operation an outermost transaction, and step 2.5 says a scope stamps every outermost transaction that
begins inside it. RP-1's per-record check admits the live head, whose image is live. Step 4.4 had two
"Done when" clauses.

**Claims and cross-references (points 15 to 19).** G-2 distinguishes reading as stored from reading
through the current schema, which V-10 maps. V-17 and G-8 state two exceptions: the change journal keeps
an erased record's identifier, and a process killed between the commit record and the discard leaves the
frame until the next open. §2.2 items 5 and 8 describe the in-place and drop paths as they are: four call
sites rewrite in place, and `DropCollection` deletes primary entries one by one without clearing them.
§3.1's reading operations are used by layers 2 to 4, not 5. §11 gains rows for SC-12, SC-12a, TR-6, TR-8
and AG-11a to AG-11f.

**One clarification after the review.** Step 7.3 names every transaction of a purge — collection-wide or
per record, the final operation pass included — among those that discard their frame, which is what V-17
already required of "purged history".

**Cut or deferred after the review.** Four things were taken out of the path without weakening a
guarantee the dissertation needs. The cumulative-bytes keyframe rule is F-12 rather than I-3: the count
rule bounds reconstruction and the large-delta ratio bounds per-version cost, and a third knob would have
added a benchmark column with no claim in Chapter 2.4 behind it. Step 6.3, the related restore, runs
after Phase 8: VR-9 is a should, the step is the largest single one, and nothing in Phases 7 to 9 depends
on it; RB-5, S-9 and N-11 move with it, and Phase 6, Phase 8 and step 8.3 say so. NF-5 measures 0 and 3
secondary indexes, not 0, 1 and 3: the middle column was a third of the runs and no decision depended on
it. HS-3's caller test is a source-level list of call sites with a reason each, not an analysis of the
compiled assemblies: the architecture test already covers the two rules over the assemblies, and
enumerating callers by IL would have been a tool in its own right. Orphan-operation removal had already
left per-record purge and erase at point 12.

## Appendix: what changed in draft 5

Nine review points, each a one-paragraph summary of a problem and the decision that answers it. All nine
match what draft 4 decides:
1. deltas, keyframes and the large-delta rule (V-1);
2. per-record logical order across restarts (V-8);
3. one persisted source for retention settings (HS-1);
4. a history collection and a version index with `Floor` (V-4, V-5);
5. a tree with tombstones and restore branches (V-9);
6. schema history and `Unmapped` (V-10, V-11);
7. purge, secure release and the journal rule (V-15, V-17);
8. versions per write, operations, references and validation before undo (V-7, V-18);
9. the live image apart from history behind `IVersionStore` (V-4, §3).

Read against the plan's own text rather than its decision list, three of those areas turned out not to
hold together, in four places.

**Versioning could never be turned off (points 3 and 7).** V-14 required purging all history first, and
RP-6's criterion began "after purging everything". But V-15 keeps the head at the purge moment by design,
so no purge can empty a history, and the change could never succeed. **V-14** now turns versioning off with
an explicit `dropHistory: true` in the same call, which removes the whole history in one transaction.
Without the flag the change is still refused, so no history disappears by accident and no gap is recorded.
**RP-6**, **N-2** and step 7.4 follow.

**Purge left the removed values in the history (point 7).** Step 3 of V-15 made a kept node whose parent was
removed into a root, but did not remove its delta. That delta's old values are exactly the removed
parent's values, so a purge kept the data it was meant to remove — and contradicted HS-5, where a root has
no delta. **V-15** now removes the delta when a node is rerooted, and says plainly that what a purge
removes is gone.

**Purge left the removed values in the journal (point 7).** V-17's journal rule covered erase and
`DropCollection`, but not purge, so the last purged record's values stayed in the journal until the next
transaction. The rule now covers purging history and dropping history too, and **RP-2** checks both files.

**The version-store contract could not drop a history (point 9).** §3.1 listed no operation to create or
drop a history collection, yet step 2.3 implemented both. HS-2 drops history with its collection, and the
architecture rule forbids anything outside the version store from touching history documents. §3.1 now
has a **Lifecycle** group — `CreateHistory` and `DropHistory` — used by `SetRetentionPolicy` and
`DropCollection` only.

## Appendix: what changed in draft 4

Seventeen review points, most of them a summary of §2.2 and of the decisions that answer it. Each was
checked against the plan's wording and, where it made a claim about behaviour, against the code. Thirteen
were already covered as stated:
- deltas with keyframes (V-1);
- per-record monotonic identifiers across restarts (V-8, HS-7);
- no in-place update of versioned content (WV-8);
- schema history and pre-migration images (V-11, WV-8);
- the version index (V-5);
- durable operation identifiers (V-7);
- `Floor` (V-5);
- the erase journal rule (V-17);
- the monotonic-source restart rule (V-8);
- schema mapping with `Unmapped` (V-10);
- restore as a branch (V-9);
- purge and erase (V-15, V-17);
- version references for undo (V-18).

Four were not, and draft 4 fixes them.

**The policy was supplied in three places, not two (point 3).** Besides the handle's property and the
unread catalogue field, `RetireRow` takes a `RetentionPolicy` argument on every call, and all seven call
sites pass `None`. §2.2 item 3 now says so, **HS-1** requires that `RetireRow` take no policy, and step 2.2
removes the parameter.

**One acceptance criterion could not pass (point 4).** Draft 3's HS-3 required a test showing that no
caller besides the seam can reach a versioned collection's content. `DropCollection` does: it retires
every record of the collection, versioned ones included, and draft 3's §2.2 item 4 said it did not.
**HS-3** now names the four paths allowed to change a versioned record's stored image — the seam,
`Rewrite`, `Erase` and `DropCollection` — and each keeps history consistent. Its test enumerates the
callers of `RetireRow`, `UpdateRow`, `RewriteRow` and `FreeItem` and fails on anything outside those
four.

**Dropped structures were not cleared (points 8 and 16).** Draft 3 claimed that every byte the engine
releases is cleared, but its list covered only record-level releases. `IndexCatalog.Drop` records a dropped
index's pages as retired without touching them, and it is also how `DropCollection` and `SetColumns`
remove indexes. `DropCollection` never touches the primary index pages, and draft 3 did not say how a
history collection is dropped at all.
- **V-17** and **RP-4** now clear every page of a dropped index, and the dropped collection's primary
  index.
- **HS-2** drops a history collection by retiring its documents, so the clearing reaches them.
- The journal rule extends to `DropCollection`, and **R-16** records the cost.

**Comments and documents had no rule of their own (point 13).** §2.2 item 13 now maps every known stale
comment and document to the step that makes it false, including the three rows of
`docs/requirements-and-plan.md` that go stale when versioning lands. §10 makes correcting them part of
the same step, and step 8.3 ends with a search for anything left behind. One correction moved earlier:
`DbEntities.RetentionPolicy`'s comment becomes false at step 3.2, not 5.2.

## Appendix: what changed in draft 3

Fifteen review points. Nine of them restated what draft 2 already does, and were checked rather than
taken on trust: write amplification (V-1, V-12, Phase 5), operation grouping (V-7), unmappable schema
history (V-10, V-11), declared element keys (V-2), purge on the tree (V-15), refusing to turn versioning
off (V-14), outgoing-only related restore (V-16), the `IVersionStore` boundary (§3, HS-3), and
`BPlusTree.Floor` (V-5). Checking them against the code found four real gaps, two of them in claims draft
2 made about the code, and five smaller ones.

**The journal was not truncated at commit (point 10).** Draft 2's V-17 said the existing protocol
truncates the journal at commit. It does not: `CommitPages` appends a commit record, and the frame stays
until the next transaction begins or the next open recovers. After an erase, the journal would still
hold the erased values as before images, and G-8's test would have failed. §2.2 item 11 records the fact.
**V-17** adds the rule: a transaction that erased anything discards its frame once its commit record is
durable. RP-5 and G-8 now hold without closing the connection, and **R-15** covers the protocol change.

**Version order did not survive a restart (point 6).** `RecordIdentity` keeps its last identifier in a
static field, so after a restart with the clock behind the last write, a new version would sort before
its record's head — and `Floor` would return the wrong version. §2.2 item 12 records it. **V-8** and
**HS-7** take the greater of `Next()` and the successor of the record's latest version. That guarantees
order where it matters, per record, without writing a high-water mark on every commit.

**`Rewrite` could discard stored information of a version (point 13).** Draft 2 called `Rewrite` a change of
representation only. But migrating a stored image drops removed columns' values and converts retyped
ones, so for a head whose history held no image, `GetStoredAsOf` would silently lose them — against G-6.
**WV-8** now requires `Rewrite` to store a head's image in its node first, whenever the mapping would
report anything unmapped, and says a stored image is never replaced by a later copy.

**Undo named only the first constraint blocker (point 14).** Draft 2 checked versions before replaying, but
found unique and relation conflicts only during the replay, one at a time. **V-7** and **AJ-4** now
validate every affected record's end state before any write, and name every blocker. They also state what
end-state validation cannot see: a replay can still meet an intermediate state that collides, as when a
value changed a→b→c inside the request and b has since been taken. That refusal is atomic and named, and
**R-14** and **F-11** record what would remove it.

**Smaller gaps.**
- **Changing *k* on a collection that already has history (point 3)** was unspecified. It now rewrites
  nothing, each version keeps the bound of the *k* it was written under, and the keyframe rule says "reach
  or exceed". The defaults are measured on the benchmark's `Publication` documents, an assistant-like
  history and wide records as well as synthetic workloads, and recorded with the workload they suit
  (NF-3, step 5.1).
- **How chained schema steps compose (point 4)** is now stated: step by step, oldest first, with a value
  reported at the first step that cannot carry it (V-10, RH-6).
- **An element whose key value changed** is a removal and an insertion, and a key holding an object or
  array falls back to positions (V-2, DL-8).
- **Purge now checks each record's kept forest before committing it,** rolling the record back on failure
  rather than trusting the rerooting (RP-1, point 7).
- **Steps that touch the engine now run every suite in the solution,** not only the suites of the projects
  touched (§10, point 15).
- **One stale cross-reference was corrected:** §2.2 pointed erasure at V-15 instead of V-17.

## Appendix: what changed in draft 2

Two inputs: eighteen review points, and the finding — from reading the assistant plan against draft 1 —
that version references can replace the journal's record payloads. All eighteen points held. Fifteen
are applied as proposed. Three are applied in a different form, each with its reason.

**Granularity, and a mismatch between writes and operations (points 1 and 18).** Draft 1 chose one version
per write and left coalescing open. **V-7** closes it: versions stay per write and are never coalesced,
and an **operation** — the outermost transaction — groups them, with an identifier that survives reopening
(§2.2 item 9 found that the transaction number does not). Writing out the consequence found two traps.
Checking each write's precondition as it is replayed fails, because the undo's own restore creates a new
version. And merging a record's writes into one restore fails when they interleave with another record's
writes on a unique column. V-7 and AJ-4 now check each record once, then replay every write in exact
reverse order.

**The interval is arbitrary (point 2).** It is. *k* is now I-1, settled at step 5.1 from reconstruction time
against history size, and recorded with its evidence before anything becomes the default.

**Large deltas can be worse than images (point 3).** **V-1** adds a second keyframe rule: a delta at least
`largeDeltaRatio` of the image makes the version a keyframe that stores no delta. So no version costs more
than full copy. The ratio is I-2.

**`GetAsOf(moment)` scans (point 4) — applied differently.** The point proposed a backward chain or another
temporal structure. A backward leaf chain is a page-format change, the one the entity-query plan's Q-3
declined. **V-5** adds `BPlusTree.Floor` instead: one descent remembering the nearest left subtree, plus at
most one more descent. That makes as-of lookups two descents at most, with no format change, and HS-4 and
RH-3 bound the pages read.

**Timestamps coupled to identifiers (point 5).** **V-8** separates logical time, which orders versions and
answers as-of questions, from recorded time, which is the wall clock and is shown to people. It explains
why as-of uses the first, and makes a clock that stepped back visible in `History`.

**Schema history is incomplete (point 6) — applied differently.** Rather than versioning the catalogue
(F-6), **V-11** keeps schema nodes and relation nodes in the history collection. Besides completing the
history, this removes a draft-1 mechanism: `Rewrite` no longer has to keep migration steps in the
`_collections` document for history's sake, and draft 1's risk of that document outgrowing its page is
gone. `SchemaAsOf` (RH-9) follows from it, and so does following relations as declared at the moment in a
related restore.

**Diff across schema changes is fragile (point 7).** **V-10** is now a table of rules, one row per kind of
schema step, and every read that maps returns a structured `Unmapped` list. RH-6 makes each row a test,
including a removal followed by a new column of the same name, which draft 1 did not consider.

**Arrays of entities (point 8).** **V-2** states positional semantics as the default and adds optional
matching by a declared element key (DL-8), falling back to positions when keys are missing or duplicated.
Paths stay positional in both modes, so replay exactness does not depend on the choice.

**Purge and branches (point 9).** **V-15** defines purge as four steps on the tree, states a three-part
invariant, and proves it. It also admits that a purged history can be a forest, which draft 1's "becomes a
root" wording hid.

**Disabling (point 10).** **V-14** chooses purge first. The gap model is F-1, with what it would need.

**Incoming relations (point 11).** **V-16** excludes them explicitly, along with finding a holder through
history. Both are F-2.

**The erasure boundary (point 12).** **V-17** names what is inside — the database file and its journal, with
secure release on every page kind — and what is outside: process memory, the operating system, the file
system, backups, and anything sent to a model. It states the guarantee in terms a test can check. The
assistant's wider boundary (diagnostics inside, conversations outside) is part of V-18 and AJ-7.

**Write amplification (point 13) — applied differently.** The point proposed benchmarking before the default,
and adaptive storage. The large-delta rule is the adaptive part, and attribution moved from every node to
one document per operation (V-12). Instead of a numeric threshold that would flip the default
automatically, **Phase 5** measures realistic workloads and step 5.2 flips the default only after sign-off
(V-13, I-4). The default was a considered choice, and should be revisited as one rather than by a rule.
Lazy insert nodes are prepared as F-8, in case the numbers need them.

**A second storage subsystem (point 14).** **§3.1** defines `IVersionStore` as the whole of layer 1's
contract, and HS-3 makes an architecture test enforce it. **WV-10** states four invariants tying live
records, nodes and index entries together; they are checked after every fault-injection point and every
model-test operation.

**Too many responsibilities (point 15).** **§3** separates six layers, with rules for what each may call.
Restore, retention and erasure, and the assistant are layers above a minimal core, and secure release
belongs to the page layer, not to versioning.

**Decisions mixed into steps (point 16).** **§4.1** classifies every decision as blocking,
implementation-time or future. Every blocking one is settled in this draft, and the Phase 3 entry gate (§9)
forbids settling a new one in code.

**Tests of mechanism, not meaning (point 17).** **§6** adds ten guarantees phrased as promises to a caller.
Each is tested only through the public surface, and step 8.2 shows that breaking a requirement fails the
guarantee test, not only a mechanism test.

**The assistant's journal (V-18).** Draft 1 kept the journal's own copy of changed values beside history.
Draft 2 replaces the values of record changes with version references. That removes the payload caps for
records, one of the two copies an erase had to scrub, and the agreement test between the two copies.
Writing the reversibility rule down exposed a claim in the assistant plan that was already false — that
record inserts and updates can always be undone — and V-18 gives three counterexamples. AJ-2 moves the
few version operations undo needs onto the assistant's contract, for both backends, so orchestration tests
against `MemoryStorage` exercise the same semantics.
