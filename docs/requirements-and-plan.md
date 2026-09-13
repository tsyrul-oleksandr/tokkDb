# TokkDb — Software Requirements and Implementation Plan

**Purpose.** This document turns the design concepts of TokkDb (§1)
into an implementable specification for the TokkDb solution, records what the code
currently does, and lays out a phased plan that closes the distance between the two.

**Scope.** The whole solution in this repository: the storage engine
(`TokkDb`, `TokkDb.Disk`, `TokkDb.Buffer`, `TokkDb.Pages`, `TokkDb.Documents`,
`TokkDb.Values`, `TokkDb.Configuration`) and the LLM application stack
(`TokkDb.LLM.Core`, `TokkDb.LLM.Storage`, `TokkDb.LLM.Application`).

**Status.** Draft 1, written against the working tree of 2026-09-04.

---

## 1. Source of requirements

Every requirement below is traceable to one of the design concept areas listed here.
The following abbreviations are used in the `Source` field of each requirement.

| Tag | Concept area | Statement that generates requirements |
|---|---|---|
| C1.1 | Foundations | The IT is defined as the automated conversion of unstructured/semi-structured user information into structured data, with verification, storage, search, update and context-dependent use by an LLM. Bidirectional circulation of data. |
| C1.2 | Processing methods | Combined processing method: deterministic procedures for format checking, parsing, normalisation and validation; LLM for semantic interpretation and conversion into a given structure. Mandatory validation of the produced structure **before** it is stored. |
| C1.3 | Storage models | Polystore combination: object/file store for originals, document store for text and variable structures, relational store for critical structured data, vector store for embeddings, graph store when needed, log store for monitoring. Shared identifier and provenance across all representations. The applied problem: the gap between a researcher's local files and a repository / URIS record (FAIR, OAI-PMH, ORCID, DOI). |
| C1.4 | Context management | Combined context model built per request as an "information-management event": current message, N last turns, structured dialogue state, short summary, user profile, relevant document fragments, structured records from storage, service instructions, tool results. Dynamic assembly under a context-window budget, with relevance, recency and access rights taken into account. |
| C2.1 | Event stream | Event tuple, event type set, ordered stream, state as a fold of events, event time vs processing time, immutability with compensating events, projections, integrity constraints, per-event hash and hash chaining, three event levels with a mapping function to semantic events, bounded (batch) and unbounded (stream) modes. |
| C2.2 | Document NoSQL architecture | Layered architecture: bits → file-system blocks → DB pages → documents → collections → indexes → transactions. Block, page-to-block mapping, page structure, collection, page affinity to a collection. |
| C2.3 | Generalised multi-level model | Multi-level model with physical, page, document, collection, index, transaction and distribution (`D`) levels; its specialisation for a local research database with history of changes and synchronisation with an institutional repository (Σ); write path and read path; consistency of the mappings between levels. |
| C2.4 | Versioning | Versioning as a **built-in property of the DBMS**, not of application code: automatic delta computation, delta element, version node, branching, version tree over a B-tree-like structure, periodic full snapshots combined with deltas, versioning of related records. |

---

## 2. Current state of the project (audit)

### 2.1 Storage engine

| Component | What exists | Assessment against the design |
|---|---|---|
| `TokkDb.Disk` | `DiskManager/DiskReader/DiskWriter`, page-granular read/write of a single file | Matches the physical level of the multi-level model (C2.3) only partially: no free-space management, no block state, no checksum, and no durability control — `DiskWriter.WritePage` opens a fresh `FileStream` per page and never flushes to the device |
| `TokkDb.Buffer` | `PageBuffer`, `BufferSlice`, `BufferReader/Writer`, typed primitives | Sound basis for the serialisation layer of the document level |
| `TokkDb.Pages` | `BasePage` (index + type header), `BaseItemsPage` (slot directory growing down from the page end, `FreeBytes`, `NextFreePosition`, `ItemsCount`), `DataPage` (linked list via `NextPageIndex`), `MetadataPage`, `PageManager`, `DataPageManager`, `MetadataPageManager` | Implements the page structure of C2.2 partially: header + slot array + record area exist; **no checksum area**, no free-space compaction, no overflow handling, no page-level delete/update |
| `TokkDb.Pages.Transactions` | `Transaction` collects dirty pages, `Commit()` writes them, `Rollback()` drops them; nested transactions via `Parent` | Not a transaction level in the sense of C2.3: **no journal**, no redo/undo, no crash recovery, no durability guarantee, no isolation |
| `TokkDb.Documents` | `ObjectDocument`, typed document values (null, int, string, ulid, object, array), binary serializer, a small path/expression language with filter selectors | Matches the document level of C2.3; the type set is narrower than `TokkDb.Values.ValueTypeEnum` declares |
| `TokkDb` façade | `TokkDbConnection`, `DbEntities<T>` with `Insert`, `GetAll`, `Get(expression)` | `Update` is an **empty stub**; there is **no `Delete`**; `GetHistories()` returns an empty array; `Get()` loads every page and filters in memory |
| `TokkDb.Configuration` | Page size 8192, metadata page index 0 | Fine; page size should become configurable per database file |
| Indexes | none — `TokkDbEntityConfiguration` contains only `//todo indexes...` | The index level of C2.3 and the index list of DC-4 are entirely unimplemented |
| Versioning | none | Versioning (C2.4), the central feature of the design, has **no implementation at all** |
| Event log | none | The event stream (C2.1) has no implementation in the engine |
| `TokkDb.Values` | leftover directory holding only `bin/` and `obj/`; there is no `.csproj` and the solution does not list it, while `ValueTypeEnum` lives under `TokkDb.Documents/Values` in namespace `TokkDb.Values` | Abandoned extraction; remove the directory or finish the move |
| Tests | round-trip tests for buffer, disk, page, document, items page, database | Good direction, but no crash, concurrency, overflow, or performance tests |

### 2.2 LLM application stack

| Component | What exists | Assessment |
|---|---|---|
| `TokkDb.LLM.Storage` | `IStorage` with collections, columns, relations, records, display rules and a declarative query; `MemoryStorage` fully implemented with validation, semantic types, schema versioning | A well-formed logical storage contract — this is the natural seam for the engine |
| `FileStorage` | every method `throw new NotImplementedException()` | The only persistent backend is a placeholder; **the application currently loses all data on exit** |
| `StorageRuntime` / `StorageBackend` | switch between `Memory` and `File` | Needs a third backend, `TokkDb`, and the switch should become a startup-time choice, not a runtime toggle over incompatible states |
| `TokkDb.LLM.Core` | LLM providers (HTTP/Ollama), `MicrosoftAgentOrchestrator` over Microsoft.Agents.AI, chat workflow, semantic type agent, document processing workflow with human-in-the-loop schema-change confirmation, diagnostics service, conversation JSON serializer | Strong match for C1.2 (combined method) and for the confirmation part of C1.1 |
| Ingestion | `.csv` and `.xlsx` only | C1.1/C1.2 require PDF, DOCX, HTML, TXT and layout-aware parsing; the largest functional gap on the processing side |
| Conversation history | `InMemoryConversationHistoryService` — explicitly documented as losing everything on exit | C1.4 requires cross-session memory; currently impossible |
| Retrieval | none — no embeddings, no vector index, no full-text index | C1.3 and C1.4 both require it |
| Provenance | none — records carry no link back to the source file, extraction run, or event | C1.3 requires a shared identifier across representations |
| Export / interoperability | none — no Dublin Core, OAI-PMH, ORCID, DOI, BibTeX | C1.3 and the Σ component of C2.3; this is the applied value of the project |
| `TokkDb.LLM.Application` | MAUI shell with Chat, Database, Diagnostics and Settings pages | Adequate; needs pages for version history and the event log |
| `docs/adapter` | empty directory | The adapter between the two halves is planned but not started |

#### Backend differences found by the Phase 4 contract suite — 2026-09-05

`TokkDb.LLM.Storage.Tests/StorageContractTests.cs` runs the subset of `IStorage` that
`TokkDbStorage` implements — `CreateCollection`, `GetCollectionDefinition`,
`GetCollectionDefinitions`, `Create`, `GetById`, `GetAll`, `Update`, `Delete` — against
both `MemoryStorage` and `TokkDbStorage`. Where the two disagree the suite asks the backend
which behaviour it has instead of asserting one of them, so every difference below is a
named property in that file rather than a comment. Both backends pass.

None of these is a bug in either implementation, because `IStorage` never said which
behaviour was required. That is the finding: the contract is defined by whichever backend
the caller happened to be running against, and `StorageBackend` switches between them.

| Behaviour | `MemoryStorage` | `TokkDbStorage` | Decision needed |
|---|---|---|---|
| Record identity | `Ulid` | `Ulid` | **Settled here.** `IStorage` changed from `Guid` to `Ulid` per D-1; the `Guid` ↔ `Ulid` mapping was rejected because it works, and so would have survived unnoticed into Phase 7 with the application speaking one identity and the engine another |
| Field validation on write | unknown column, wrong type and read-only column rejected with `StorageValidationException` and structured errors | none | Validation lives in `MemoryStorage`, not in the contract. It must move to a shared place or become a stated obligation before `StorageBackend.TokkDb` is the default (Phase 7) |
| A duplicate in a unique column | refused, `StorageValidationException` carrying a `UniqueConstraint` error naming the column | refused, `UniqueConstraintViolationException` naming the column and the record already holding the value | **Narrowed 2026-09-05 by DC-4's unique indexes.** It was "one backend enforces it, the other does not"; both now refuse the write and only the exception type differs, so what is left is the error-type question below rather than a missing constraint |
| A value of the wrong type | rejected on write | accepted on write and read back as what it was | **Settled 2026-09-07.** It used to be accepted and then unreadable forever — the column type decoded the stored value, four types were stored as text, and a Guid column meeting text that is not a Guid threw on every read, taking the whole collection with it because a scan decodes every record. The stored form is now self-describing for every type, so a wrong value is a wrong value rather than a poisoned record. Whether the adapter should also *reject* it is the `ValidatesRecordFields` row above, still open |
| Missing field with a column default | filled from `ColumnDefinition.DefaultValue` | left absent | Defaults are a logical concern; the engine's `ColumnDescriptor` carries one but nothing applies it |
| `CollectionDefinition.Metadata` | preserved | preserved | **Settled 2026-09-05 in Phase 7.** `Metadata` and `DisplayRule` are documents in `_settings` and `_displayRules`, not fields of the structural descriptor — a note about a collection changes for reasons the schema does not, and putting it in the catalogue document would rewrite the document the data chain and index roots live in every time one changed. `SemanticTypeName` and `ValidationPatterns` are column properties and do sit on the column descriptor, stored uninterpreted the way `Description` is |
| Collection name case | `OrdinalIgnoreCase` — `customer` finds `Customer` | `Ordinal` — `Customer` and `customer` are two different collections that can both exist | Pick one and state it. The engine's catalogue is the harder one to change later, because the name is a key in a stored document |
| Creating a collection twice | `InvalidOperationException` | `ArgumentException` | Error types are not part of the contract at all today; a caller cannot catch portably. The unique-violation row above is the same question in a second place, which makes it the one worth settling first |
| `GetAll` order after a delete | not insertion order — the `Dictionary` reuses the freed bucket | not insertion order — the free-space map reuses the freed slot (ST-1) | Neither preserves it, so the contract should say `GetAll` is unordered rather than leaving callers to discover it |
| `Int64`, `Decimal`, `DateTime`, `Guid` values | stored as themselves | stored as themselves | **Settled 2026-09-07.** `LongDocumentValue`, `DecimalDocumentValue`, `DateTimeDocumentValue` and `GuidDocumentValue` exist, and nothing is written as text any more. A decimal keeps its scale (stored as the four ints `GetBits` gives, so 1.50 is not 1.5) and a `DateTime` keeps its `Kind` (`ToBinary`, so a value written as UTC does not come back Unspecified). `QueryPredicate.IsIndexable` no longer excludes ordered comparisons over these four — it now means what it says, that the constants can be an index key at all, which an object and an array cannot. A reader still accepts the old text form so a database written before this is not unreadable; an index built over one of these columns before it holds text keys and has to be dropped and recreated |

Where the two already agree, the suite pins the behaviour so it stays agreed: addressing an
unknown collection is an `InvalidOperationException` on every entry point; `GetById` of an
unknown id is `null`, not an error; updating a record that is no longer there returns
`false` rather than throwing; a second `Delete` of the same record returns `false`; a null
field value round-trips as a present field whose value is null; and `GetCollectionDefinitions`
shows no system collections.


### 2.3 The central structural problem

The repository contains **two independent halves that do not reference each other**.
`TokkDb.LLM.Storage` does not depend on `TokkDb`; the engine is exercised only by
`TokkDb.Example.Console` and its unit tests, and the application persists nothing.
The design (§1) describes a single information technology in which the document
engine *is* the storage of the intelligent dialogue system. Until the engine is the
implementation behind `IStorage`, the software does not deliver that design.

**Consequence for the whole plan: closing this seam is what the engine work builds
towards. It is validated early by the walking-skeleton adapter in Phase 4 and
completed in Phase 7; Phases 1–3 exist to make an engine worth adapting to.**

---

## 3. Target architecture

```
                    MAUI application (chat, database, diagnostics, versions, events)
                                        |
                    TokkDb.LLM.Core  — agents, workflows, context assembly
                                        |
        +---------------------------+---+---------------------------+
        |                           |                               |
  Ingestion pipeline        Context engine                  Export / interop
  (parse, normalise,        (window + summary + state       (Dublin Core, OAI-PMH,
   LLM structuring,          + profile + retrieval)          ORCID, DOI, BibTeX)
   validate)                        |
        |                           |
        +---------------------------+
                     |
              IStorage (logical contract: collections, columns, relations,
                        records, queries, versions, events)
                     |
              TokkDbStorage  (the adapter — new)
                     |
   TokkDb engine:  documents -> collections -> indexes -> transactions/journal
                     -> pages -> blocks -> single local file
                     + event log + version tree
```

Three cross-cutting services sit beside the levels rather than inside one of them:
the **event log** (C2.1), the **version store** (C2.4) and the **provenance graph**
(C1.3). All three are engine-level so that the guarantee is a property of the DBMS,
as the design requires, and not of the calling application.

---

## 3.1 Settled design decisions

These were settled on 2026-09-04 and are binding for everything below. They exist
because each one is far cheaper to decide before the file format is frozen than after.

**D-1 — Record identity is `Ulid`.**
One identity for the whole system. `Ulid` is already minted by
`DocumentSerializer.Create`; it is time-ordered, so primary-index inserts land at the
right edge of the tree instead of splitting pages at random positions, which a `Guid`
key would do. The `Guid` used by `IStorage` is mapped at the adapter boundary — the
engine never sees it.
*Consequence:* `IStorage`'s record identifier changes to `Ulid`, or the adapter keeps
a deterministic `Ulid ↔ Guid` mapping. The first is cleaner and is the default choice.

> **Qualified by measurement, 2026-09-05.** "It is time-ordered" is true only to the
> millisecond. `Ulid.NewUlid()` draws a fresh random part on every call, so identifiers
> minted inside one millisecond sort against each other at random — and a bulk load happens
> inside a handful of milliseconds, which makes every identifier in it random. Measured over
> 100 000 inserts into the primary index: 543 leaf splits and 183 entries per leaf for
> `Ulid.NewUlid()`, against 484 and 206 for wholly random bytes. The premise the decision
> rests on did not hold, and the `Guid` it rejected would have been no worse.
>
> `TokkDb.Pages.Records.RecordIdentity.Next()` restores it, by the ULID specification's own
> monotonic rule: inside a millisecond the previous identifier is incremented rather than a
> new random part drawn. The same 100 000 inserts then cost **308 leaf splits and 324
> entries per leaf** — the tree an ideal strictly ascending sequence produces, half the
> leaves of the random case and one level shallower. D-1 stands, but only because the
> identifier is minted this way; `Ulid.NewUlid()` on its own does not satisfy it.

**D-2 — Index entries point to `(pageId, slotId)`, never to a byte offset.**
The slot array is the indirection: a record may move inside its page during
compaction without any index being touched. Physical pointers — data page head and
tail, primary index root, secondary index roots, free-space root — are held in the
collection's catalogue entry.
*Layering note:* `CollectionDefinition` in `TokkDb.LLM.Storage` stays **logical**
(name, description, columns, metadata, display rule). It is the contract the agent
tools see, and storage internals must not leak into it. Physical pointers live in the
engine-side catalogue document described in D-4.

**D-3 — Index keys use an order-preserving binary encoding.**
Byte comparison must equal semantic comparison, so signed integers have the sign bit
flipped, `DateTime` is normalised to ticks, and decimals are encoded to sort
correctly. Strings are compared **ordinally over an explicitly normalised form**
(case and diacritic folding applied once at write time and stored), rather than
depending on culture-sensitive comparison at read time — Ukrainian text makes an
implicit collation a source of results that differ between machines.
Long string keys are prefix-truncated, with the full predicate re-checked against the
record. Secondary indexes store the composite key `(value, recordId)` so duplicates
need no posting lists.

**D-4 — The catalogue is stored as documents in system collections (Option B).**
Page 0 holds only a small fixed root: magic number, format version, page size, and
the location of the `_collections` collection. Everything else — including the
definition of `_collections` itself — is an ordinary `ObjectDocument` stored through
the same page, index and transaction machinery as user data:

```
page 0 (root)  →  _collections     one document per collection: id, name,
                                   description, columns, schemaVersion,
                                   dataFirstPage, dataLastPage,
                                   primaryIndexRoot, secondaryIndexRoots,
                                   freeSpaceRoot, recordCount,
                                   historyCollectionId, retentionPolicy
                  _indexes         index descriptors
                  _relations       relation definitions
                  _semanticTypes   semantic type registry
                  _displayRules    display rules
                  _settings        per-collection AI and application settings
                  (later: _events, _versions)
```

*Why:* adding a field to a collection's metadata becomes adding a field to a
document — no new binary reader/writer and no format break. The LLM layer accumulates
metadata continuously (semantic types, display rules, AI settings, later retention
settings), so this is the difference between a routine change and a migration. It
also makes §4.9 of step 2 — storing `SemanticType`, `DisplayRule` and per-collection
AI settings — fall out with no additional mechanism, and the catalogue inherits
indexes, transactions and, later, versioning for free.

*Obligations this creates:*
- Bootstrap: the code carries a hardcoded minimal descriptor for `_collections`, or
  `_collections` is schemaless and validated in code only. Nothing else may be
  hardcoded.
- The catalogue is loaded into memory at open and cached; field access must not cost
  a B+Tree lookup.
- A catalogue mutation and the data change it accompanies commit in **one**
  transaction. Creating a collection inserts a document into `_collections` and
  allocates its first page atomically — another reason the journal (TX-2) precedes
  the index work.
- The `_` prefix is reserved; user collections beginning with it are rejected.
- This replaces the current `MetadataPage`, whose `EntitiesCount` is cast to `byte`
  and which must fit one 8 KiB page.

**D-5 — Versioning is deferred; deletion is real; the format stays compatible.**
Delta versioning, version trees, history collections and time-travel reads (§4.5) are
**not implemented in this pass**. `Delete` removes the record: its key leaves every
index and its space is reclaimed. The version model of C2.4 remains an open question.

Four pieces of insurance keep it a later addition rather than a later format break —
they are requirements VR-11 and VR-12 below, and they are **Must** now:

1. Update is copy-on-write even without versioning (VR-12).
2. The record header carries the version fields from the start, unused (VR-11).
3. The root page carries a format version number (ST-9).
4. Deletion is routed through one internal method that a version store can later
   intercept (VR-12).

*Open, and to be decided before the evaluation (Phase 13):* whether history stores
deltas (C2.4, the approach the design proposes) or full copies (the approach it
rejects for duplication). Versioning is the central feature of the design, so the
implementation has to exist before the evaluation results are produced, not merely
before release. Nothing in D-5 forecloses either choice.

**D-6 — Free-space management is required now, not later.**
Copy-on-write updates (VR-12) and real deletes (D-5) both produce dead space from the
first release. This moves ST-1 from "eventually" to the same phase as update and
delete.


## 4. Functional requirements

Each requirement has an identifier, a statement, the concept area it comes from, and
acceptance criteria (AC) that can be checked by an automated test or a demonstrable
scenario. Priority: **M** = must (needed to meet the design goals), **S** = should,
**C** = could.

### 4.1 Event stream (Group EV)

**EV-1 (M, C2.1).** The system shall record every state-changing operation as an
immutable event stored in an append-only log inside the database file.
*AC:* after any `Insert`/`Update`/`Delete`/schema change, a new event is present in
the log; no API exists that rewrites or removes an existing event; an attempt to
write into an occupied log position fails.

**EV-2 (M, C2.1).** An event shall be the tuple
`(id, timestamp, source, type, objectId, payload, version)`, where `id` is unique
and monotonically assigned, `source` is one of user, application, server module,
file system, DBMS, LLM or external resource, and `version` is the version of the
information object after the action.
*AC:* the serialised event carries all seven fields; writing an event with an empty
`id`, a duplicate `id`, a missing `source`, or a `version` not greater than the
previous version of the same object is rejected (this is the formal integrity set
of C2.1).

**EV-3 (M, C2.1).** The event type set shall include, at minimum:
`Create`, `Read`, `Update`, `Delete`, `FileUploaded`, `TextExtracted`,
`RecordStructured`, `RecordValidated`, `RecordStored`, `ContextSearched`.
*AC:* the full document-import scenario produces exactly the event sequence
`FileUploaded → TextExtracted → MetadataGenerated → RecordValidated → RecordStored`
for one object, and each event is retrievable by `objectId`.

**EV-4 (M, C2.1).** Each event shall carry both an occurrence time and
a processing time, with `t_processing ≥ t_event`.
*AC:* an event replayed from a delayed source (batch import of an old file, later
synchronisation) shows a non-zero difference; queries can filter by either clock.

**EV-5 (M, C2.1).** Errors shall be corrected by appending a compensating event that
references the event being corrected and states the reason, never by editing history.
*AC:* correcting a wrongly extracted publication year appends
`PublicationYearCorrected` with `correctsEventId` and `reason`; the original event is
still readable; the current state reflects the correction.

**EV-6 (M, C2.1).** The current state of any information object shall be
reconstructible by folding its events from the initial state.
*AC:* for a randomly generated sequence of ≥ 1000 operations, the state rebuilt from
the log is byte-identical to the materialised state.

**EV-7 (S, C2.1).** Each event shall carry a hash of its own content and the hash of
the preceding event in the same stream.
*AC:* modifying any stored event byte makes verification fail from that event
onward; a `VerifyLog()` operation reports the first broken position.

**EV-8 (S, C2.1).** Events shall be classified into file-system,
database and semantic application levels, and a projection function shall map
technical events to semantic ones so that only semantically meaningful changes enter
the main object history.
*AC:* writing an intermediate temporary file produces a file-system event but no
semantic event; the object timeline shown in the UI contains only semantic events.

**EV-9 (S, C2.1).** The system shall support multiple projections built from one
stream (current record view, object timeline, export view, statistics).
*AC:* adding a new projection requires no re-parsing of source documents and is
built from stored events alone.

**EV-10 (S, C2.1).** Both a bounded (batch) mode over a finite set of local files and
an unbounded (stream) mode reacting to events as they occur shall be supported.
*AC:* importing a folder of N documents runs as one bounded stream with a start and
an end and a final report; ordinary interactive use appends to the unbounded stream.

### 4.2 Storage engine — physical and page levels (Group ST)

**ST-1 (M, C2.2).** The physical level shall manage block allocation and
release with an explicit free-space structure, and shall track block state
(free / occupied / damaged / reserved).
*AC:* deleting documents returns space that a later insert reuses; the file does not
grow monotonically under a delete-heavy workload.

**ST-2 (M, C2.3).** Every page shall carry a checksum; a page whose checksum does not
match on read shall be reported as damaged rather than parsed.
*AC:* a deliberately corrupted byte in a data page causes a typed
`PageCorruptedException` naming the page index, not a silent wrong result.

**ST-3 (M, C2.2).** The page structure shall be
`header | slot array | record area | free space | control area`, with the header
holding page type, identifier, record count, free-space boundaries, owning collection
and checksum.
*AC:* the current `BaseItemsPage` layout is extended with the collection identifier
and the control area; existing round-trip tests pass against the new layout, and a
format version number in the metadata page identifies the layout.

**ST-4 (M, C2.2).** Records shall be addressable through the slot array so that a
document can move inside a page without changing its logical identifier.
*AC:* compaction of a page moves records but leaves every document reachable by the
same id.

**ST-5 (M, C2.3).** Documents larger than the usable page area shall be stored in
overflow chains.
*AC:* a 1 MB document round-trips through insert and read; today this throws
`PageOverflowException`.

**ST-6 (M).** In-place update and delete of documents shall be supported.
*AC:* `DbEntities<T>.Update` and `Delete` change stored state, are covered by tests
and emit the corresponding events; the current empty `Update` body is removed.

**ST-7 (S, C2.3).** A buffer pool with a defined replacement policy shall sit between
the page level and the disk.
*AC:* repeated reads of the same page cause one physical read; the hit ratio is
reported in diagnostics; the pool size is configurable.

**ST-8 (S, C2.2).** The page size shall be a per-database setting written into the
metadata page rather than a compile-time constant.
*AC:* a database created with 4 KiB pages and one with 16 KiB pages both open
correctly; the value is validated as a multiple of the file-system block size.

**ST-9 (M, D-5).** The root page shall carry a magic number and a format version, and
opening a database with an unknown format version shall fail with a clear diagnostic
rather than misparse.
*AC:* a file written by a later format is refused by name and version; the version is
visible in diagnostics.

### 4.3 Storage engine — document, collection and index levels (Group DC)

**DC-1 (M, C2.2).** A collection shall be a named set of documents with a
membership mapping, and a page shall belong to exactly one collection.
*AC:* the metadata page records the collection of every data page; a full scan of a
collection reads only its own pages.

**DC-2 (M, C2.3).** The document level shall support nested objects, arrays and the
declared scalar type set, with serialisation and validation as separate concerns.
*AC:* `ValueTypeEnum` and the implemented `IDocumentValue` set agree; a document with
three levels of nesting and an array of objects round-trips exactly.

**DC-3 (M, C2.3).** Schema shall be optional but expressible: a collection may carry
a validation schema, and documents shall be validated against it on write when one is
present.
*AC:* inserting a document that violates the collection schema is rejected with a
typed error naming the field; inserting into a schemaless collection succeeds.

**DC-4 (M, C2.3).** Primary and secondary indexes shall be supported, with
secondary indexes over at least title, authors, year, DOI, keywords, institution and
document type.
*AC:* a lookup by DOI over 100 000 records touches O(log n) pages, verified by the
page-read counter, instead of scanning every page as `DbEntities.Get` does today.

**DC-5 (M, C2.3).** Queries shall be executed against indexes where possible instead
of materialising every document.
*AC:* the path-expression filter is compiled to an index scan when an index covers
the predicate; diagnostics report which access path was chosen.

**DC-6 (S, C2.3).** Index maintenance cost shall be measurable, so that the
read/write trade-off stated in C2.3 can be demonstrated.
*AC:* a benchmark reports insert throughput with 0, 1, 3 and 5 secondary indexes.

**DC-7 (M, D-4).** The catalogue shall be stored as documents in reserved system
collections, with only a fixed root record on page 0.
*AC:* adding a new metadata field to a collection definition requires no change to
any binary reader or writer and no migration of existing databases; `_collections`
describes itself; a user attempt to create a collection whose name begins with `_` is
rejected.

**DC-8 (M, D-4).** A catalogue mutation and the data change it accompanies shall
commit in one transaction.
*AC:* an injected failure between inserting the `_collections` document and
allocating the collection's first page leaves no half-created collection after
reopening.

### 4.4 Transactions and recovery (Group TX)

**TX-1 (M, C2.3).** A transaction shall commit changes to documents, indexes, the
event log and page structures atomically, or leave none of them applied.
*AC:* an injected failure between writing a document and updating its index leaves
the database in the pre-transaction state after reopening.

**TX-2 (M, C2.3).** A write-ahead journal shall be maintained, and opening a database
shall replay committed transactions and discard uncommitted ones.
*AC:* killing the process at randomly chosen points during a 10 000-operation run
never leaves an unopenable or partially updated database; the recovery decision is
logged.

**TX-3 (M).** `Rollback` shall restore the pre-transaction state including pages that
were already flushed, not merely drop the in-memory set.
*AC:* a rollback after a page eviction still leaves no change visible.

**TX-4 (S).** The isolation level offered shall be stated explicitly and enforced
(single-writer, multiple-reader is sufficient for a local database).
*AC:* concurrent readers never observe a partially applied transaction; a second
writer is either serialised or rejected with a typed error.

### 4.5 Versioning (Group VR) — the core feature

> **Status (D-5).** VR-1 … VR-10 describe the target state and remain the
> requirements the versioning design (C2.4) is measured against. Their **implementation is
> deferred**: this pass implements real deletion instead. VR-11 and VR-12 are the
> part that is **Must now** — they cost almost nothing today and are what stops
> versioning from becoming a file-format break later.


**VR-1 (M, C2.4).** Version history shall be produced by the DBMS itself: an
application performs ordinary write operations and does not construct versions.
*AC:* `IStorage.Update` alone causes a new version to exist; no application code
references a version table; `GetHistories()` stops returning an empty array.

**VR-2 (M, C2.4).** On every change the system shall compute the delta
between the previous and the new state, where each delta element is
`(path, operation, oldValue, newValue)` and `path` addresses nested elements such as
`product.manufacturer.address.city`.
*AC:* changing one field of a 50-field document stores a delta with exactly one
element; the stored delta size is a small fraction of the full record size.

**VR-3 (M, C2.4).** Array changes shall be represented with operation, index or
element identifier, and value.
*AC:* inserting into the middle of an array, removing an element and reordering are
each represented distinctly and replay exactly.

**VR-4 (M, C2.4).** A version node shall carry
`(versionId, parentVersionId, createdAt, author, delta, metadata/comment)`.
*AC:* the version log of a record renders as an audit trail answering when, by whom
and how each change was made — the first purpose stated in C2.4.

**VR-5 (M, C2.4).** History shall be a tree, not a line: restoring an
earlier state and continuing to edit shall create a branch, leaving the newer
versions intact.
*AC:* after restore-and-edit both branches are listed, both are reconstructible, and
neither was deleted.

**VR-6 (M, C2.4).** Version storage shall combine deltas with periodic full
snapshots, and the snapshot interval shall be configurable.
*AC:* reconstruction cost from any version is bounded by the snapshot interval;
a benchmark reports reconstruction time against interval size.

**VR-7 (M, C2.4).** The system shall expose reading a record as of a version and as
of a timestamp, and diffing any two versions.
*AC:* `GetAsOf(id, version)`, `GetAsOf(id, time)` and `Diff(v1, v2)` return correct
results on a reference INSERT/UPDATE/DELETE scenario.

**VR-8 (M, C2.4).** Deletion shall be logical: a delete creates a version marking the
record deleted; earlier versions stay readable and the record can be restored.
*AC:* a deleted record is absent from ordinary queries, present in history, and
restorable to its last state.

**VR-9 (S, C2.4).** Point-in-time restore shall be consistent across related records,
so that an object composed of several records is restored as a whole.
*AC:* restoring a publication to a past moment restores its author and source links
to the state they had at that moment.

**VR-10 (S, C2.4).** The version index shall use a B-tree-like structure, and its
storage overhead shall be measurable.
*AC:* a benchmark reports history size against number of versions and compares it
with the full-copy approach.

**VR-11 (M now, D-5).** Every stored record shall carry, from the first release, a
header containing `recordId` (Ulid), `versionId` (Ulid), a `previousVersion` pointer
`(pageId, slotId)`, a `flags` byte with at least `Live`, `Superseded` and `Deleted`,
and a `schemaVersion`. Only `recordId`, `flags` and `schemaVersion` are read in this
pass; `previousVersion` is written as zero.
*AC:* the header is present and round-trips; enabling versioning later requires no
rewrite of records in existing databases. Cost is roughly 25–30 bytes per record.

**VR-12 (M now, D-5).** Update shall be copy-on-write and deletion shall be routed
through a single internal entry point governed by a retention policy.
An update writes a new record image, repoints the primary index, marks the previous
image `Superseded`, and — under `RetentionPolicy.None`, the only policy implemented in
this pass — returns its space to the free list. Under a later
`RetentionPolicy.KeepVersions` the superseded image is retained and linked through
`previousVersion` instead.
*AC:* no code path mutates a record in place; a crash between writing the new image
and repointing the index leaves the old record intact and readable; switching the
retention policy is the only change needed to begin retaining versions; deletion
happens in exactly one method, which a version store can later intercept.

**VR-13 (S, D-5).** Scans shall skip superseded and deleted images without consulting
an index.
*AC:* a full collection scan reads the `flags` byte and skips dead images; the record
count reported by the catalogue matches the number of live images.

### 4.6 Ingestion and structuring (Group IN)

**IN-1 (M, C1.1, C1.2).** Supported input formats shall include PDF, DOCX, TXT, MD,
HTML, CSV and XLSX; images with OCR are optional.
*AC:* each format reaches the structuring stage in an end-to-end test; today only CSV
and XLSX are accepted.

**IN-2 (M, C1.2).** Preprocessing shall detect the file type, extract content, remove
service elements, normalise text, segment it and detect its language, as a pipeline
of separately testable stages.
*AC:* each stage is a unit-testable component and its result is recorded as an event.

**IN-3 (M, C1.2).** Extraction shall preserve document structure — headings, tables,
lists, captions, reading order and the source page or position — and carry it into
the structuring stage.
*AC:* a value taken from a table cell keeps a reference to the table, row and page it
came from; loss of layout is the specific failure to avoid.

**IN-4 (M, C1.2).** The combined method shall be implemented in the stated
division of labour: deterministic code for format checking, parsing, normalisation
and validation; the LLM for semantic interpretation and mapping to the target
structure.
*AC:* no LLM output reaches storage without passing deterministic validation; the
diagnostics timeline shows the two kinds of step distinctly.

**IN-5 (M, C1.1, C1.2).** Every structured record shall be validated against the
collection schema and semantic types before it is stored; failures shall be
quarantined with a reason rather than silently dropped.
*AC:* the invalid-record list already modelled in `DocumentProcessingContext` is
persisted, is reviewable in the UI, and each entry can be corrected and re-submitted.

**IN-6 (M, C1.2).** Schema changes proposed during import shall require explicit user
confirmation before they are applied.
*AC:* the existing `ChangeSchema`/`ConfirmSchemaChange` flow is exercised by an
integration test; nothing is persisted before approval.

**IN-7 (M, C1.3).** One identifier shall connect the original file, the extracted
text, each structured record derived from it, its versions and the events involved.
*AC:* from any record the UI can reach the source file, the extraction run and the
event timeline, and vice versa; C1.3 names this as the condition for determining
provenance and re-running individual procedures.

**IN-8 (S, C2.1).** Batch import of a folder shall be supported as a bounded stream
with progress, cancellation and a final report.
*AC:* importing a personal archive of ≥ 100 files completes with a per-file outcome
table, and cancelling leaves no partially imported record.

**IN-9 (S, C1.3).** Duplicate detection and record matching shall be applied when new
records are added (author name variants, title normalisation, DOI equality).
*AC:* re-importing the same publication under a different filename and a differently
spelled author is reported as a probable duplicate rather than inserted twice.

**IN-10 (C, C1.2).** Open information extraction into subject–relation–object triples
shall be available for content that does not fit a predefined collection.
*AC:* triples are stored, queryable and linked to their source fragment.

### 4.7 Context management (Group CX)

**CX-1 (M, C1.4).** Context shall be assembled per request from the nine components
listed in C1.4 (current message, recent turns, structured dialogue state, summary,
user profile, relevant document fragments, structured records, service instructions,
tool results).
*AC:* the assembled context is inspectable in diagnostics as a list of contributing
components with their token cost.

**CX-2 (M, C1.4).** Conversation history shall be persisted in TokkDb and survive
application restart, across sessions.
*AC:* `InMemoryConversationHistoryService` is replaced by a storage-backed
implementation; after restart, previous conversations and their state are available.

**CX-3 (M, C1.4).** A sliding window over recent turns shall be combined with a
rolling summary of older turns, with the summary regenerated on a defined trigger.
*AC:* in a 200-turn conversation the request size stays within the configured budget
and a constraint stated in turn 3 is still respected in turn 150 — the specific
failure of a bare sliding window.

**CX-4 (M, C1.4).** A structured dialogue state (goal, current stage, entities,
completed operations, missing data) shall be maintained and updated after each turn,
independently of the free text.
*AC:* the state is readable and editable as data, is persisted, and drives the next
action selection.

**CX-5 (M, C1.3, C1.4).** Retrieval shall combine exact search over structured
records with semantic search over text fragments, using a vector index stored inside
the database file.
*AC:* embeddings are generated locally, stored with the shared object identifier, and
a semantic query returns fragments ranked by similarity together with their source.

**CX-6 (M, C1.4).** Context assembly shall respect an explicit token budget with a
priority order, and truncation shall be recorded, not silent.
*AC:* when the budget is exceeded, the diagnostics event names what was dropped and
why; C1.4 requires prioritisation rather than a larger window.

**CX-7 (S, C1.4).** Access rights and relevance shall be applied as filters during
context assembly.
*AC:* records marked restricted never appear in an assembled context.

**CX-8 (S, C1.4, C2.1).** Every context assembly shall itself be recorded as a
`ContextSearched` event with the identifiers of what it included.
*AC:* an answer can be traced to the exact record versions that informed it.

### 4.8 Interoperability and export (Group EX)

**EX-1 (M, C1.3, C2.3 Σ).** A metadata model for research outputs shall exist
covering publication, author, institution, project, dataset and source, with the
attributes (type of result, authorship, institution, date, venue,
identifiers, keywords, project link).
*AC:* the model is a set of TokkDb collections with relations, created by a
first-run migration.

**EX-2 (M, C1.3).** Persistent identifiers shall be supported: DOI for outputs, ORCID
for researchers, and a stable internal identifier for every object.
*AC:* identifiers are validated by format, are unique where applicable, and survive
export and re-import.

**EX-3 (M, C1.3).** Export shall produce a metadata record in a Dublin Core /
OAI-PMH-compatible form, losslessly derived from the stored record.
*AC:* exporting and re-importing a record yields an equal record; the transformation
before transfer to a repository must be lossless.

**EX-4 (M, C1.3).** Before export the system shall validate completeness against the
target system's requirements and report exactly which attributes are missing.
*AC:* a record without a DOI, venue or institution produces a specific, actionable
report rather than a failed submission — this is the problem C1.3 identifies as the
cause of poor repository population.

**EX-5 (S, C1.3).** Import from ORCID, BibTeX, RIS and CSV shall be supported, with
normalisation and duplicate matching against existing records.
*AC:* importing an ORCID works list updates existing records instead of duplicating
them.

**EX-6 (S, C1.3).** A FAIR self-check shall be reported per record (findable,
accessible, interoperable, reusable), based on the presence of identifiers,
metadata, vocabularies and provenance.
*AC:* the check is shown in the UI and exportable as a report.

### 4.9 User interface (Group UI)

**UI-1 (M).** The database page shall show collections, records, and the full version
history of a record with a diff view and a restore action.
*AC:* the version tree of VR-5 is visible and navigable.

**UI-2 (M).** An event-log page shall show the object timeline built from semantic
events, with filtering by object, type and time.
*AC:* the import scenario of EV-3 is visible as a five-step timeline.

**UI-3 (S).** The import flow shall show the pipeline stages, the proposed schema
changes awaiting confirmation, and the quarantined invalid records.
*AC:* a user can approve, reject or correct at each stopping point.

**UI-4 (S).** Diagnostics shall report storage-level metrics: page reads, buffer hit
ratio, index usage, file size, history size.
*AC:* the numbers needed for the evaluation (Phase 13) are obtainable
from the running application.

---

## 5. Non-functional requirements

**NFR-1 Local-first (M, C1.1, C1.3).** The system shall operate fully on one personal
computer with no server component, storing everything in one database file, and shall
work with a local LLM (Ollama) without network access.
*AC:* a full import → structuring → query → export cycle completes with networking
disabled.

**NFR-2 Performance (M).** Reference targets on a personal machine with 100 000
records: single-record lookup by indexed field < 10 ms; insert of a structured record
< 5 ms excluding LLM time; opening a database < 500 ms; reconstruction of any version
< 50 ms.
*AC:* a repeatable benchmark project reports these numbers; they are the input to
the evaluation (Phase 13).

**NFR-3 Durability (M, C2.3).** No committed transaction shall be lost, and no
partially applied transaction shall survive, across process kill or power loss.
*AC:* the fault-injection suite of TX-2 passes over ≥ 100 randomised runs.

**NFR-4 History efficiency (M, C2.4).** Delta-based history shall be measurably
cheaper in storage than full-copy versioning for the reference workloads.
*AC:* a benchmark compares both strategies over the same operation sequence and
reports the ratio.

**NFR-5 Integrity (S, C2.1, C2.3).** Corruption of any page or event shall be
detectable, and the affected object identifiable.
*AC:* `VerifyDatabase()` reports damaged pages and the first broken event position.

**NFR-6 Privacy (M, C1.3).** No user data shall leave the device except through an
explicit export or synchronisation action initiated by the user.
*AC:* the only outbound calls are to the configured LLM endpoint and, when the user
triggers it, the export target; both are visible in diagnostics.

**NFR-7 Portability (S).** The engine shall target .NET 9 with no platform-specific
dependencies; the application shall run on macOS, Windows and Android.
*AC:* CI builds and runs the test suite on each target.

**NFR-8 Testability (M).** Every requirement marked M shall be covered by at least one
automated test or a scripted demonstrable scenario.
*AC:* a traceability matrix maps requirement identifiers to test names.

**NFR-9 Observability (S).** Storage, ingestion and context operations shall emit
diagnostic events with a consistent structure.
*AC:* the existing `DiagnosticsService` is used by all three layers, not only the
application layer.

**NFR-10 Configurability (C).** Page size, buffer pool size, snapshot interval,
context budget and LLM operation settings shall be configurable per database.
*AC:* settings are persisted in the metadata page or the settings file, validated,
and visible in the UI.

---

## 6. Explicitly out of scope

These follow from the design concepts but are deliberately not implemented; documentation should
present them as model-level generalisation rather than as properties of the software.

| Item | Area | Decision |
|---|---|---|
| Distribution level `D`: sharding, replication, load balancing, consistency under network partition | C2.3 | Not implemented. The system targets the **local** level (C1.1). The model retains the level; the implementation is single-node. `D` is realised only as Σ — synchronisation with an external repository (C2.3). The interfaces must not preclude a later distributed backend. |
| Graph database for a knowledge base | C1.3 | Relations between collections cover the needed cases; a graph store is stated as an option, not a component. |
| Separate relational store alongside the document store | C1.3 | One document engine with schema validation and relations covers the "critical structured data" role. This is a deviation from the polystore recommendation of C1.3 and **must be justified explicitly in the architecture documentation**, not left implicit. |
| Multi-user access control | C1.4 | A single-user local system; CX-7 is enforced as record-level visibility flags only. |
| OCR of scanned documents | C1.2 | Optional (IN-1); implement only if time allows. |

---

## 7. Gap analysis — design vs. code

| # | Design element | Present in code | Requirement that closes it | Effort |
|---|---|---|---|---|
| 1 | Storage engine backs the intelligent dialogue system | No — the two halves are unconnected; `FileStorage` throws | ST/DC/TX groups; Phases 4 and 7 | High |
| 2 | Event stream as the primary record (C2.1) | No | EV-1…EV-10 | High |
| 3 | Built-in DBMS versioning with delta and version tree (C2.4) | No — `GetHistories()` returns empty | VR-1…VR-10, **deferred by D-5**; VR-11…VR-13 keep the format compatible | High |
| 4 | Index level of the layered model (C2.3) | No — `//todo indexes` | DC-4…DC-6 | Medium |
| 5 | Transaction level with journal and recovery (C2.3) | Partial — dirty-page set only | TX-1…TX-4 | Medium |
| 6 | Page checksum / integrity control (C2.2, C2.3) | No | ST-2, EV-7, NFR-5 | Low |
| 7 | Free-space management, delete, in-place update | No | ST-1, ST-6 | Medium |
| 8 | Large documents (overflow) | No — throws | ST-5 | Medium |
| 9 | Processing of PDF/DOCX/HTML with layout retention (C1.2) | No — CSV/XLSX only | IN-1…IN-3 | High |
| 10 | Deterministic validation before storage (C1.2) | Partial — validation exists, but only for the CSV/XLSX path | IN-4, IN-5 | Low |
| 11 | Cross-session memory and dialogue persistence (C1.4) | No — in-memory only | CX-2 | Medium |
| 12 | Vector store and semantic retrieval (C1.3, C1.4) | No | CX-5 | Medium |
| 13 | Combined context model, nine components (C1.4) | Partial — history and tools only | CX-1, CX-3, CX-4, CX-6 | Medium |
| 14 | Shared identifier and provenance across representations (C1.3) | No | IN-7, EV-8 | Medium |
| 15 | Export to repository / URIS, FAIR, OAI-PMH, ORCID, DOI (C1.3, C2.3 Σ) | No | EX-1…EX-6 | Medium |
| 16 | Batch and stream modes (C2.1) | Partial — interactive single file | IN-8, EV-10 | Low |
| 17 | Measurable performance and storage characteristics for the evaluation | No benchmarks | NFR-2, NFR-4, DC-6, VR-10 | Medium |

Further consistency notes:

- C2.2 and C2.3 require that a page belongs to one collection. The engine chains data
  pages per entity, which satisfies this; but the property is currently accidental
  rather than recorded in the page header. ST-3 makes it explicit so that the design
  and the implementation agree.
- `TokkDb.Values` exists only as a directory of stale build output with no project
  file, while `ValueTypeEnum` lives in `TokkDb.Documents/Values`. Either finish the
  extraction or delete the directory before the code is submitted for review.

- `DiskWriter` opens a new `FileStream` for every single page write. Beyond the
  performance cost this makes the durability requirements (TX-2, NFR-3) impossible to
  state precisely, because nothing controls when data reaches the device. A single
  owned file handle with an explicit flush point at commit is a prerequisite for
  Phase 2.

---

## 8. Implementation plan

Each phase states its goal, its content, and an exit criterion that can be
demonstrated. The order below follows the decisions in §3.1: the catalogue comes
first because everything is stored through it, the journal comes before indexes
because indexes multiply the pages one commit has to touch, and versioning moves
after the adapter because D-5 defers it.

**Critical path: Phases 1–7, then 8 and 9.**

### Phase 0 — Decisions — *complete*

D-1 … D-6 in §3.1. Recorded so later phases do not reopen them.

### Phase 1 — File format v2 and the catalogue

*Goal:* the database describes itself, and metadata can grow without a format break.

- Root page: magic number, format version, page size, location of `_collections`
  (ST-8, ST-9, D-4).
- `_collections` as a self-describing system collection; the hardcoded bootstrap
  descriptor; `_indexes`, `_relations`, `_semanticTypes`, `_displayRules`,
  `_settings` created empty (DC-7).
- Catalogue cache loaded at open; the `_` prefix reserved (DC-7).
- Page checksums and `PageCorruptedException` (ST-2); the extended page header with
  owning collection id and control area (ST-3).
- `DiskWriter` replaced by a single owned file handle with an explicit flush point,
  instead of a fresh `FileStream` per page write.
- `MetadataPage` and its `byte`-capped entity count retired.

*Exit:* a database is created, closed and reopened; its collection definitions are
read back from `_collections`; a corrupted page is reported by index rather than
silently parsed.

### Phase 2 — Journal and recovery

*Goal:* multi-structure changes are atomic before anything multiplies them.

- Write-ahead journal, commit protocol, replay of committed and discard of
  uncommitted transactions on open (TX-1, TX-2).
- `Rollback` restores the pre-transaction state including already-flushed pages
  (TX-3); the current implementation only drops an in-memory set.
- Catalogue mutation and data change in one transaction (DC-8).
- Single-writer / multiple-reader isolation stated and enforced (TX-4).
- Fault-injection harness: kill the process at randomised points during a write.

*Exit:* ≥ 100 randomised kill-during-write runs leave an openable, consistent
database, and the recovery decision is logged each time.

### Phase 3 — Mutable records

*Goal:* records can be changed and removed, with the version door left open.

- The record header of VR-11: `recordId`, `versionId`, `previousVersion`, `flags`,
  `schemaVersion` — written now, mostly unread.
- Copy-on-write update and single-entry-point deletion under
  `RetentionPolicy.None` (VR-12); `DbEntities<T>.Update`'s empty body removed and
  `Delete` added (ST-6).
- Free-space map and block states (ST-1, D-6) — now required, because both
  copy-on-write and delete produce dead space immediately.
- Overflow chains for records larger than the usable page area (ST-5); today this
  throws `PageOverflowException` and extracted text will exceed 8 KiB routinely.
- Page compaction with stable slot identity (ST-4); scans skip dead images by
  `flags` (VR-13).

*Exit:* insert, update and delete round-trip; a 1 MB document stores and reads back;
a delete-heavy workload does not grow the file monotonically; a crash between writing
a new image and repointing the index leaves the old record readable.

### Phase 4 — Walking-skeleton adapter — *checkpoint, deliberately throwaway*

*Goal:* validate the contract between the halves while changing it is still cheap.

- ~200 lines of `TokkDbStorage : IStorage` covering `CreateCollection`, `Create`,
  `GetById`, `GetAll`, `Update`, `Delete` only.
- Run the existing `MemoryStorageTests` against it.
- Settle the `Ulid` ↔ `Guid` question in practice (D-1) and confirm the column
  definition maps cleanly onto the catalogue document shape.

*Exit:* the shared test suite passes against both backends for the covered subset.
Whatever mismatch this surfaces is fixed here, not after the B+Tree is written
against the wrong assumptions.

### Phase 5 — B+Tree indexes

*Goal:* the index level of the multi-level model (C2.3) exists, persists and is measurable.

- Order-preserving key encoding for every column type, with the normalised-string
  rule of D-3.
- Persistent primary index — B+Tree pages on disk with the buffer pool as its cache
  (DC-4, ST-7). It is not an in-memory structure rebuilt by a full scan at open.
- Node split and merge, linked leaves, roots recorded in the catalogue document
  (D-2, D-4).
- Unique indexes for `ColumnDefinition.Unique`; secondary indexes with composite
  `(value, recordId)` keys; referential checks for `RelationDefinition` need an index
  on the target, so relations are planned here rather than discovered later.
- Index maintenance inside the transaction (TX-1); benchmark of insert throughput
  against index count (DC-6).

*Exit:* lookup by DOI over 100 000 records meets NFR-2 and the page-read counter
proves an index was used; opening that database stays under 500 ms.

### Phase 6 — Query planner

*Goal:* one query language, executed through indexes.

- `StorageQuery` becomes the single logical input; the `DocumentPathParser`
  expression tree stays as the internal form. Two parsers are not maintained (DC-5).
- Normalise to a conjunction of comparisons, match conjuncts against available
  indexes (equality first, then range), choose an access path, apply residual
  predicates per record.
- Evaluate residual predicates against the serialized buffer where possible, reading
  one field rather than deserializing whole documents.
- Report the chosen access path in diagnostics — this is also the measurement the
  evaluation needs (UI-4).

*Exit:* `DbEntities.Get` no longer loads every page; the access path for each query
is visible and matches expectation on a fixed test set.

**Finding — the rule is not a cost model, and it is measurably wrong past a
crossover.** "Equality first, then range, then a scan" chooses without knowing how
many records a value matches, and over 20 000 records an equality matching one in
forty cost **507 page reads against a full scan's 440**. The cause is that a data
page holds some forty-five records: a scan reads each page once and gets forty-five
records out of it, while a seek reads a page per match because the matches are
scattered and `DiskReader` caches nothing between two reads of the same page. A
selective query is the other extreme — a unique seek returning one record of 20 000
read **4 pages against 440, and 0.34 ms against 64.96 ms**.

The seek is still the faster plan at one-in-forty (27 ms against 173 ms), because
what dominates is not the page reads but the 500 documents it materialises against
the scan's 20 000 — which is the buffer-level filtering this phase added. So the rule
is kept, and the crossover is recorded by a test rather than hidden: a cost model
would need per-value counts that nothing collects, and a page cache would move the
crossover before a cost model would be worth writing. Both are Phase 8 candidates,
and the diagnostics of UI-4 are what would justify either.

### Phase 7 — Full adapter and system collections

*Goal:* the application stores everything in TokkDb.

- Complete `TokkDbStorage : IStorage`, replacing the walking skeleton.
- `SemanticType`, `DisplayRule` and per-collection AI settings persisted as documents
  in their system collections (D-4) — no new mechanism needed. **Done 2026-09-07.**
  D-4 paid off exactly as claimed: the whole of the storage side is
  `SystemDocumentStore`, which reads, writes and removes `ObjectDocument`s in a
  reserved collection through the pages, journal and transactions that already
  existed. What each document *means* stays a layer up — `SemanticTypeDocument` maps
  a definition field by field rather than storing an opaque blob, so a field added to
  a semantic type is a field added to a document, and `_semanticTypes` describes its
  own columns the way `_collections` does.
- Structural schema and AI-derived metadata kept in separate documents, so
  frequently-changing AI metadata does not force a schema migration. **Done.** The
  separation is testable rather than asserted: rewriting a collection's metadata and
  re-registering its semantic types any number of times leaves `schemaVersion`
  where it was, and adding a column moves it. That matters because `schemaVersion` is
  what a record is read against (VR-11) — moving it because a confidence score
  changed would say every stored record now means something else.
- Dynamic collection creation and editing with **lazy migration**: `schemaVersion`
  per record, reads upgrade on the fly, an explicit rewrite command for eager
  migration. Eager rewriting on every type change would bloat the file. **Done
  2026-09-07.** Adding a column is metadata-only. A rename, a retype or a removal is
  recorded as a step in the collection's catalogue document, the version moves, and
  every read of a record below that version replays the steps above it — through one
  path in `DataPageManager`, so queries, scans, the index builder and the index
  maintenance a delete does all see the same upgraded record. `Rewrite(collection)`
  converges the records and then drops the log; it runs in batches rather than one
  transaction, which is safe precisely because partial progress is the state lazy
  migration already handles.

  **The indexes are rebuilt eagerly and the records are not, and the asymmetry is
  deliberate.** A query encodes its constant as the column's current type, so an index
  still holding keys made from the old one answers with the wrong records rather than
  slowly — there is no lazy version of that. But an index is keys, not records:
  rebuilding one reads the collection and writes a fraction of it, leaving no dead
  space in the data pages, which is the cost the decision exists to avoid.

  **Finding — three live page-level bugs, found by this step and all older than it.**
  Rewriting many records on the same pages is the first workload that frees and
  re-registers slots heavily, and it exposed a family of faults that all come from one
  assumption: that the items on a page lie in the order of their slots. They do, until
  a slot is handed out a second time.

  1. **A freed slot's bytes were handed out at a reconstructed position.** `FreeItem`
     kept the slot's length and discarded its position — a slot holds one or the other,
     not both — and `ReuseSlot` recovered the position by summing the lengths of the
     slots before it. One out-of-order slot makes that sum point at a *live* record,
     which the next write then overwrites. 300 records under three rounds of updates
     lost 21 of them outright. Freed bytes are now reclaimed by compaction (ST-4, which
     exists for exactly this) rather than reissued in place, and the reconstruction is
     gone.
  2. **Compaction slid records down in slot order.** Same assumption; once it is false,
     one record is copied over another that has not moved yet. It now moves them in the
     order they lie in the page, so every destination is at or below its source.
  3. **`DataPageManager` predicted which slot `RegisterItem` would use**, and modelled
     only one of its placements. The record went to a different slot than the address
     recorded for it: the record is on the page, so a scan finds it, but every index
     entry made from that address points at nothing. A regression test over 120 records
     with 60 deleted first loses 39 of 60 subsequent writes to lookups. `RegisterItem`
     now reports the slot it used.

  None was caught before because no test combined deletion, compaction and an index
  lookup, and (1) needs several rounds of updates that shrink a record. Each now has a
  test at the page level and one at the record level.
- A unit-of-work / batch transaction across the adapter, so an import of hundreds of
  records is one commit rather than hundreds, and a failed import leaves nothing.
  **Done 2026-09-07.** `IStorage.InBatch(Action)` on both backends: the engine runs it
  as one transaction, and `MemoryStorage` copies its dictionaries at the outermost
  batch and puts them back if the work throws, so atomicity means the same thing on
  either. A batch inside a batch joins the outer one.

  **Finding — a rolled-back transaction left the in-memory catalogue ahead of the
  file.** The rollback undid the pages, but the catalogue is cached from open, so a
  column the failed work added was still in the cache, and the pages it allocated were
  still counted — the next read ran off the end of the file with an
  `EndOfStreamException`. Nothing had exposed it because the existing rollback tests
  inject a crash and then *reopen*, which re-reads everything. `InTransaction` now
  re-reads the catalogues when the outermost transaction rolls back, which is the same
  thing a reopen does.
- `StorageBackend.TokkDb` becomes the default; `FileStorage` is removed or reduced to
  a JSON export. **Done.** `FileStorage` is deleted rather than reduced: every one of
  its members threw `NotImplementedException`, so it was a backend in an enum and
  nothing else. `StorageBackend` is now `Memory` and `TokkDb`, and `TokkDb` is the
  default.
- Conversation history persisted in TokkDb (CX-2) — after this phase the application
  finally survives a restart. **Done.** `_conversations` and `_conversationEntries`
  are two more reserved collections, and D-4's machinery took them without change —
  the reserved list was always expected to grow ("later: `_events`, `_versions`"), so
  `Initialize` now creates any system collection an older database is missing rather
  than needing a migration.

  One document per event rather than one per conversation: a conversation grows for as
  long as it is used, and appending to it must not mean rewriting everything said so
  far. What a conversation is ordered by — title, created, last touched, which
  conversation an event belongs to and where in it — is written as fields; the three
  rich payloads an event can carry are written as JSON, because they are the
  application's shapes, they change with the UI that renders them, and nothing asks the
  database a question about what is inside them.

  Writing a system document is also no longer capped at a page. It used to be rewritten
  where it lay, which ST-6 cannot grow; one that has outgrown its slot is now retired
  and written again, which takes an overflow chain (ST-5) when it has to.

*Exit:* the application is closed and reopened and every collection, record, schema,
semantic type, display rule and conversation is still there; the shared storage test
suite passes in full against the TokkDb backend.

**Two limits the adapter inherits and does not hide.** Dropping a collection does not
return its pages: free space is per collection (ST-1) and there is no global free-page
list, so the pages stay allocated and unreachable until a file-level compaction exists.
Because of that, owning collection ids are issued from a high-water mark kept on the
catalogue's own descriptor rather than from the maximum of the collections that exist —
otherwise dropping the newest collection would hand its id, which is written on the pages
it left behind, to the next one created. And a collection's settings are one document, so
they have to fit a page: growing a record into an overflow chain is ST-6 and unbuilt, so a
metadata map past about 8 KB is refused with the storage layer's own error rather than
silently truncated.

### Phase 8 — Versioning — *gated on the open question in D-5*

*Goal:* implement versioning (C2.4).

First settle deltas versus full copies. The design proposes deltas
(C2.4) and rejects full copies for duplication, so a full-copy
history collection would leave the central versioning feature without an implementation.

- Delta computation with paths, including nested objects and arrays (VR-2, VR-3).
- Version nodes and the version tree with branching, indexed by a B+Tree
  (VR-4, VR-5, VR-10) — the tree machinery from Phase 5 is reused.
- `RetentionPolicy.KeepVersions` switched on; superseded images retained and linked
  through `previousVersion` (VR-12). Because Phase 3 already wrote those headers, no
  existing database needs rewriting.
- Snapshot + delta strategy with a configurable interval (VR-6); time-travel and diff
  API; logical delete; related-record point-in-time restore (VR-7, VR-8, VR-9).
- A retention/collapse policy and an explicit administrative `PurgeHistory`, distinct
  from user-level delete — otherwise history grows without bound and a genuine erase
  request cannot be honoured.
- The version view in the UI (UI-1); benchmarks for NFR-4 and VR-10.

*Exit:* the reference INSERT/UPDATE/DELETE scenario (VR-7) is reproduced in the application,
including a branch created by restore-and-edit.

### Phase 9 — Event log

*Goal:* implement the event stream (C2.1).

Built after versioning because it reuses the same machinery — append-only storage,
hash chaining, ordering constraints — and because version nodes should reference the
event that produced them.

- Append-only event store in `_events`; the event tuple and its integrity constraints
  (EV-1, EV-2); the event type set emitted from the engine and the ingestion pipeline
  (EV-3); event time and processing time (EV-4).
- Compensating events (EV-5); state as a fold verified against materialised state
  (EV-6); hash chaining and `VerifyLog` (EV-7).
- The three event levels and the technical → semantic projection (EV-8); further
  projections (EV-9); the object timeline UI (UI-2).

*Boundary with Phase 8, decided once:* the event log is the durable record of *what
happened*; the version tree is the queryable record of *what an object looked like*.
Version nodes reference events; neither duplicates the other.

*Exit:* the five-step import timeline of EV-3 is visible in the application, and the
state rebuilt from the log matches the materialised state for a randomised workload.

### Phase 10 — Ingestion breadth

*Goal:* the combined processing method (C1.2) applies to real documents.

- PDF, DOCX, HTML, TXT, MD parsers with layout retention (IN-1, IN-3); today only
  `.csv` and `.xlsx` are accepted.
- The preprocessing pipeline as separately testable stages emitting events (IN-2).
- Provenance identifier threading file → text → record → version → event (IN-7).
- Batch folder import as a bounded stream with report and cancellation (IN-8, EV-10).
- Duplicate detection and record matching (IN-9).

*Exit:* a folder of mixed PDF/DOCX/XLSX publications imports end to end, each record
traceable to its source page.

### Phase 11 — Context engineering

*Goal:* the combined context model (C1.4).

- Sliding window + rolling summary + structured dialogue state (CX-3, CX-4) on top of
  the persisted history from Phase 7.
- Local embeddings and a vector index in the database file; hybrid retrieval (CX-5).
- Token budget with priority and recorded truncation (CX-6); visibility filtering
  (CX-7); context assembly recorded as an event (CX-8); diagnostics view (CX-1).

*Exit:* a 200-turn conversation stays inside the budget while still honouring a
constraint stated at its start.

### Phase 12 — Interoperability

*Goal:* the applied value stated in C1.3.

- Research-output metadata model as a first-run migration (EX-1); DOI and ORCID
  support with validation (EX-2).
- Dublin Core / OAI-PMH-compatible export with a round-trip test (EX-3);
  pre-export completeness validation with an actionable missing-attribute report
  (EX-4).
- ORCID/BibTeX/RIS import with matching (EX-5); FAIR self-check (EX-6).

*Exit:* a local archive becomes a validated export package, and the report names
precisely what a repository would reject.

### Phase 13 — Evaluation

*Goal:* the numbers and scenarios that demonstrate the system's characteristics.

The benchmark project starts in Phase 2 and grows in every later phase rather than
being assembled at the end. It must produce: insert, query and version-reconstruction
timings; history size for delta versus full copy; page reads with and without
indexes; context size and answer quality with and without each context component;
extraction accuracy per format against a manually labelled set.

*Exit:* every quantitative claim about the system is produced by a command anyone
can re-run from a clean checkout.

## 9. Mapping key capabilities to code artefacts

Each key capability should point at something that runs.

| Capability | Artefact that demonstrates it |
|---|---|
| Event-based representation of information management (C2.1) | Event store, projection function, object timeline, state-fold test |
| Layered organisation of a document NoSQL store (C2.2, C2.3) | The engine itself: block, page, document, collection, index, transaction levels, with the mapping consistency test |
| Generalised multi-level model specialised for a local research database (C2.3) | The research-output collections, their indexes, the change history and the repository export |
| Versioning built into the DBMS with a delta tree (C2.4) | The version store, the branch scenario, and the delta-vs-full-copy benchmark |
| Combined processing method (C1.2) | The ingestion pipeline with deterministic validation gating LLM output, plus per-format accuracy measurements |
| Combined context model (C1.4) | The context assembler, its nine components, and the ablation measurements from Phase 9 |

---

## 10. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Engine work (Phases 1–9) is large relative to the time available | The project is delivered as a prototype that does not run | Sequence strictly and do not reorder around the journal; Phases 1–3 and 7 are the minimum that makes the system run at all, and 8–9 the minimum that makes the central features demonstrable |
| Versioning and the event log overlap conceptually | Duplicate mechanisms, doubled storage | Decide once: the event log is the durable record of *what happened*, the version tree is the queryable record of *what an object looked like*, and version nodes reference the event that produced them |
| Versioning deferred (D-5) slips past the evaluation | Versioning, the central feature, has no implementation behind it at the point results are produced | VR-11, VR-12 and ST-9 keep the format compatible, so Phase 8 stays a bounded addition rather than a migration; set a date for the delta-versus-copy decision rather than letting it drift |
| Engine work absorbs the whole remaining schedule | The application-side requirements (C1.1–C1.4) — ingestion, context, export — arrive too thin to demonstrate the information technology | Timebox Phases 1–9; Phases 10–12 can be reduced in breadth without weakening the design, but not skipped |
| LLM output instability breaks import | Unreliable demonstrations | IN-4/IN-5 already require deterministic validation to gate everything; add a fixed-seed offline fixture set for tests so the suite does not depend on a live model |
| Polystore recommended in C1.3 vs. single-engine implementation | A design-review question with no prepared answer | Justify it explicitly in the architecture documentation (see §6 of this document): one engine with schema validation, relations, versions, events and a vector index covers the roles that C1.3 distributes across stores, at the local scale the system targets |
| Benchmarks left to the end | No evaluation results | Start the benchmark project in Phase 2 and extend it in every later phase |
