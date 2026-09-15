# TokkDb Assistant — requirements and development plan

**Status.** Draft 10, written 2026-09-14 against the working tree of that date, on which
`docs/versioning-requirements-and-plan.md` has landed through its Phase 9. Draft 8 moved the undo of
record changes onto the engine's version history (that plan's V-18), and draft 9 aligned it with that
plan's draft 5, so every blocker is found before anything is written. Draft 10 records the landing where
this plan still said "has still to build", settles two open items of §10 against what the engine now
provides, and records why four future items of that plan stay future. The appendices at the end list
what changed.

**The interface.** Six screens are drawn in `docs/design/` and published as a canvas:
`Main` (chat and the step diagram), `Overview`, `Records`, `Record`, `WhatItKeeps`, `Confirm`.
They are the reference for groups UI and BR, and they lift their type, spacing, radii and
palette from `TokkDb.LLM.Application` rather than inventing a look.

**What this is.** A new application in which a person keeps things in a *storage* and talks
to it. They never name a collection, write a query, or issue a command. They say "I want to
save this" and attach a file, or ask "what did I spend on conferences last year", and the
system works out where the data belongs, puts it there, and shows them what it did.

**What this is not.** It is not a rewrite of `TokkDb.LLM.Application`. That application stays
in the repository, keeps working, and is available to read. Nothing here references it.

---

## 1. Scope

### 1.1 In scope

| | |
|---|---|
| Data the user brings | Pasted text, `.txt`, `.docx`, `.csv`, `.xlsx` |
| What the user can do | Store it, find it, correct it, remove it, and change how it is organised |
| How they do it | By saying what they want, in a chat. No commands, no syntax, no schema vocabulary |
| What they see | The chat, and beside it a diagram of how the request was handled, step by step, clickable |
| Where it all lives | One TokkDb database file: their data, their conversations, and every trace |

### 1.2 Out of scope for this stage

PDF and HTML ingestion; images; vector search and semantic retrieval; multi-user or shared
storage; synchronisation between devices; export to external repositories; browsing a record's
history and time travel (the engine versions every collection the assistant writes to, and the
assistant uses those versions only to undo, to show a change's before and after, and to erase —
D-17, TR-6, NF-4d; whether the browser should show a history is §10's item 14); phone layouts.

### 1.3 The user's mental model

This is the constraint that everything else answers to. To the user there is **one storage**.
They do not know there are collections, that a collection has columns, that two collections
can be related, or that anything is indexed. They should not have to learn. Every word in the
interface is theirs — *things you have stored*, not *collections*; *what each one looks like*,
not *schema*.

The consequence for design: the system must be willing to decide. A question like "which
table should this go in?" is a question the user cannot answer, because they do not know the
tables exist. The system decides, does it, and shows the decision in a form they can
disagree with afterwards.

**And yet they can look.** Not having to know how something is stored is not the same as not
being allowed to see it. Someone who has put a year of conference records into the storage
will want to scroll through them, sort them by cost, and check one against a receipt — and
none of that is a database task, it is looking at your own things. So there are two surfaces.
The **conversation** is where work happens and where nothing structural is ever named. The
**browser** is where everything is visible: every thing stored, every record in it, and, on
demand, what each one keeps — described in the user's words rather than in column types.

The line between them is what the user must *know*, not what they may *see*. Nobody needs the
browser to use the application. Someone who never opens it can store, find and correct
everything by saying so. It is there for looking, checking and reassurance, which is a real
need and not a lesser one: a person who cannot see what a system did with their data does not
trust it.

---

## 2. Decisions

These were settled before the document was written. Each records what was chosen and why, so
that a later change is a decision rather than a drift.

**D-1 — New projects in this solution, over `TokkDbConnection`, with no `TokkDb.LLM.*`
dependency.** The assistant defines its own storage contract and its own schema definitions,
implemented directly on the engine's `TokkDbConnection`. It does not reference
`TokkDb.LLM.Storage`, `TokkDb.LLM.Storage.Engine`, `TokkDb.LLM.Core` or
`TokkDb.LLM.Application`.

*Why:* the existing contract was shaped by a walking skeleton and carries decisions made
under different constraints. A new contract can be designed knowing everything §2.2 of
`requirements-and-plan.md` found out the hard way, rather than inheriting the compromises
that produced those findings.

*Consequence:* field serialization, query translation, semantic types, display rules and
conversation history are all built again. That is a phase of work, not an afternoon. The old
code remains readable and should be read before each is rebuilt — the findings are in it.

**D-2 — The engine's own facilities are used directly and gladly.** `TokkDbConnection`,
`SystemDocumentStore`, `DescribeSystemCollection`, the query planner and its `QueryReport`,
the B+Tree indexes, `InTransaction`, and the reserved system collections are all engine
features, not `TokkDb.LLM.*` features. The assistant uses them.

*Consequence:* the assistant needs new reserved collections — for diagnostics, for the change
journal, for request state and for conversations, which have three different retentions between
them (TR-7, TR-8, SC-10) and so do not all belong in one. **How many is settled in step 3.2 and
recorded there**, rather than guessed here; an earlier draft of this decision said two, before
the trace model had a change journal or a request state in it. That is a change to
`TokkDb.Pages/CollectionCatalog/SystemCollections.cs`, which D-4 of the engine anticipated
("later: `_events`, `_versions`"), and which `Initialize` already handles for databases written
before the name existed. It is the only engine change this plan calls for; anything else that
turns out to be needed is raised rather than assumed.

**D-3 — Local models are the design target; the budget assumes them.** Ollama, small models
(the current default is `qwen3.5:4b`), context raised to 64k.

*Why:* a small model is not a large one with less room. It degrades when crowded long before
the window is full, it follows instructions worse the more of them there are, and it produces
malformed structured output under load. Designing for it produces an application that is also
cheap on a cloud model; designing for a cloud model produces one that does not work locally.

*Consequence:* 64k is the ceiling, not the target. Section 6 sets the real budgets, which are
far below it.

*Measured, and not yet true.* Ollama's default here is **4,096**, and `options.num_ctx` is
dropped silently by its OpenAI-compatible endpoint — the endpoint the framework speaks
(`/v1` gave 4,096, `/api/chat` gave 16,384, same parameter). So "raised to 64k" describes an
intention, not the current state. It takes `OLLAMA_CONTEXT_LENGTH`, a Modelfile, or an
`IChatClient` that speaks Ollama natively, and the third also fixes the output cap. See
`scratch/AgentFrameworkSpike/FINDINGS.md`. **Raising the context and bounding the tool loop
have to land together**: today the 4,096 window is the only thing ending a runaway loop, badly.

*Decided, step 0.3 (2026-09-14).* The transport is native: OllamaSharp's `IChatClient` over
`/api/chat`, with `Microsoft.Agents.AI` above it, and `num_ctx`, `num_predict` and `think` are
per-call options set from the operation's declaration. Both limits are enforced (`/api/ps` reports
the window asked for; a cap of 5 produces 5 tokens), tool calling works with the loop bounded at
two iterations (3 round trips against 23), schema-constrained output is as good (14 of 20 with
thinking on, as before; 20 of 20 with it off), and the approval path behaves (20 of 20). The
operations set 16k; §6.2's budgets sit far below it. `scratch/TransportGate/FINDINGS.md`.

**D-4 — More models, each doing less.** Rather than one model that does everything, the
pipeline uses specialised calls: a classifier for intent, an extraction model for turning
prose into candidate records, a mapping model for deciding where data belongs, a phrasing
model for the reply. Each sees only what its job needs.

*Why:* it is the strongest token lever available and it is also a quality lever. A model asked
to do one thing with one tool and one small context does it better than the same model asked
to do four things with twenty tools.

**D-5 — Tools are scoped per operation, and some operations get none.** The model is never
handed the whole tool surface. An ingestion step sees the mapping tools; a retrieval step sees
one query tool; a correction step sees the record tools. The catalogue of tools exists in C#;
what reaches a model is a subset chosen by the operation.

*Measured, and it changes the emphasis.* The first tool costs 252 prompt tokens and each
further one about 56 — most of it is the chat template's tool-calling preamble, paid once for
having any tool at all. So scoping from five tools to one saves about 220 tokens, and dropping
the last one saves 250. The larger win is **none**, where the operation allows it: putting the
storage digest in the system message cost 181 tokens against the 252 of the tool that would
have fetched it, and removed a 25-times cost multiplier with it.

*The rule that follows:* **no read tool whose result could have been inlined.** A tool that
fetches invites a loop — the model judges the answer insufficient and asks again, measured at a
median of 22 calls — while a tool that acts terminates. Where tools are used at all,
`MaximumIterationsPerRequest` is set explicitly; its default of 40 stopped nothing.

**D-6 — Retrieved records do not enter the model's context.** The model writes a structured
query. C# validates it against the schema and runs it. The rows are rendered in the chat by
the application. They go back to a model only if the user asks a question *about* them, and
then as a digest — counts, ranges, a few examples — not as rows.

*Why:* retrieval is the commonest operation and the one whose cost would otherwise scale with
the answer. A thousand-row answer should not cost more than a one-row answer.

**D-7 — Additive changes happen; destructive changes ask.** Creating a collection, adding a
column, storing records: done, and shown in the trace. Removing a column, changing a type in
a way that can lose values, deleting records, deleting a collection: a confirmation card in
the chat showing what would be lost, in the user's words, with counts.

*Why:* asking about every addition would defeat the premise that the user gives no commands.
Not asking about a loss would be indefensible.

*Superseded in part by D-14.* The binary turned out to be too coarse: several changes that
delete nothing still change what stored records mean or make future writes fail. D-14 splits
the middle out. D-7's two ends stand, and so does its reason for existing.

**D-8 — Everything the assistant does is recorded as a trace, in the database.** One trace per
user request. Steps carry their name, their timing, their inputs and outputs, and for data
changes what changed: for a record, the versions before and after, from which the values are
shown (D-17); for a structural change, the change itself. Model calls record the model, the token counts and a
hash of the prompt — not the prompt text.

*Why:* the diagram is the product feature and the trace is the diagnostic record and the
token-budget evidence, and they are the same thing. Storing prompt text as well would put the
user's content in the database twice and make traces larger than the data they describe.

**D-9 — The diagram is drawn, natively.** A `GraphicsView` over `Microsoft.Maui.Graphics`,
with the layout computed by ordinary testable C# and the drawing kept thin.

*Why:* the blocks are clickable, the diagram grows while the request is running, and it has to
look the same on two platforms. A web view would be quicker to a first picture and slower to
everything after it.

*Consequence, and a correction to how the spike's result was read.* The layout is held in
device-independent points, and **one transform, produced at draw time from the view's current
scale, is used by both the drawing and the hit testing**. The spike found tap and canvas
coordinates identical on a density-2 display, which is true of that display and says nothing
about a window dragged onto a monitor with a different scale. Sharing the transform makes them
agree by construction rather than by coincidence (TR-5a).

**D-10 — Desktop first: Mac Catalyst and Windows.** Phone targets stay out of the project file
until there is a reason to add them.

**D-11 — One database file.** User data, conversations and traces together, so a data change
and the `DataChange` record describing it commit in the same transaction (the engine's TX-1),
and so there is one file to copy. Diagnostic steps are written independently as they happen
(TR-4a); only the audit record is inside the mutation's transaction.

*Consequence:* one file means one writer. AG-8a states that assumption and enforces it at open,
rather than leaving a second instance to corrupt the first.

**D-12 — The verification discipline of this repository carries over**, plus a token-budget
harness. xUnit throughout; a deterministic fake model so orchestration is testable without
Ollama running; contract tests for the new storage interface; and a harness that asserts the
tokens per request for a fixed set of scenarios, so a regression in the central
non-functional goal fails a test instead of being noticed in a month.

**D-13 — The user may see everything, and needs to understand none of it.** A browser shows
every thing stored and every record in it, with structure available on demand and written in
plain words. It is a second surface, not the primary one, and nothing in the conversation
requires having opened it.

*Why:* section 1.3. Hiding the data would be paternalistic and would cost trust; making the
user go through it would cost the premise.

*Consequence:* every string in the browser is subject to UI-2 as strictly as the chat is, and
the browser must not become the place where work is done. It reads; it changes only through
the same rules and the same trace as everything else (D-7, D-8).

**D-14 — A change is classified by what it can do, not by what it is called.** Three classes.
**Safe**: no stored value can be lost and no meaning can change. **ReviewRequired**: nothing is
deleted, but the meaning of existing records changes or future writes can start failing.
**Destructive**: stored values are lost or become unreadable.

*Why:* D-7 read "additive" as "safe" and that is false in at least four ways that matter here.
A newly unique field can refuse writes the schema previously allowed. A newly required field
makes every existing record incomplete. A new relation makes the engine enforce integrity on
every subsequent write. A semantic type changes normalisation, which silently changes which
records a query *matches*. None of those deletes anything.

*Consequence:* the classification carries **evidence**, not just a label. "This would make 3 of
your 47 records invalid, here they are" is answerable; "this is a semantic change" is not, and
would reintroduce exactly the nagging D-7 existed to avoid. Violation counts are computed
before the question is asked, never after.

**D-15 — A request is a resumable state machine, not only a trace.** Every request carries a
state: `Running`, `WaitingForUser`, `Resuming`, `Completed`, `Cancelled`, `Failed`. It is
persisted with the request and survives a restart.

*Why:* a trace records what happened and cannot continue. D-7 and D-14 both pause a request
mid-flight to ask, so the pause is a normal state rather than an exception, and a crash during
one leaves "asked" with nothing able to act on the answer.

*Consequence:* `WaitingForUser` holds the **validated, resolved intent**, never the
conversation. A yes then executes exactly what was shown, without paying for a model again and
without the risk of a second answer differing from the first. This is also what makes the
confirmation honest: the thing confirmed is provably the thing executed.

**D-16 — The query model is shared infrastructure, and has two faces.** One rich internal model
— projection, filter, sort, limit, cursor, aggregation, relation traversal — used by C#, the
browser and the follow-up path. One deliberately narrower model-facing subset, tight enough for
a grammar to constrain, which C# expands into the internal form.

*Why:* the same abstraction serves retrieval, the browser's table, sorting and filtering,
follow-up questions and aggregation. Specified as one sentence it will be discovered piecemeal
and end up as four incompatible half-models. Specified as one rich thing the model must emit,
it becomes a small SQL that a 4B model has to get right, which the spike gives no reason to
expect.

*Consequence:* the browser can grow powerful without making the model's job harder, and the
internal model can gain features without changing what the model must learn. Aggregation is
computed by the engine and never by a model.

**D-17 — Compensation is a matrix, not a blanket promise. Record changes are undone through
versions; structural changes through the journal.** A request that changed data can be reversed,
for the operations the matrix says so, in one of two ways:
- **Record changes are undone from version history.** Every collection the assistant writes to is
  versioned by the engine (SC-12a), and each record change's `DataChange` names the version it
  replaced and the version it produced. An insert is undone by deleting the record; an update or
  delete is undone by restoring the replaced version, under the record's original identity, so
  relations survive.
- **Structural changes are undone from the journal.** Adding a field is undone by removing it;
  removing or retyping one is undone from the inverse the journal keeps, where it can keep one.

*What the earlier blanket wording could not survive.* Four cases break it, and all four are ordinary.
- Removing a field from a wide collection needs every removed value, which exceeds any payload cap
  TR-2b can set.
- Retyping is lossy in one direction by construction.
- Putting a record back can violate a uniqueness rule or a relation created since — and that is true
  of an undone update or insert as well as a delete. A's email goes x→y, then B takes x; undoing A
  would put x back beside B. A's conference goes c1→c2, then c1 is deleted; undoing A would point at
  nothing. Undoing an import deletes a conference that a later expense now references, and restrict
  refuses.
- A record written twice in one request cannot be undone by checking each write's precondition as it
  is replayed, because the compensation's own restore creates a new version, so the earlier write's
  check would never pass. The check is made once per record before anything is replayed (AG-11a).

*So reversibility has three values, decided per operation:*
- **Reversible**: nothing can invalidate the undo. A record change on a collection with no unique
  column and no relation in either direction; a structural change whose complete inverse is kept and
  still valid.
- **ReversibleWithConditions**: the undo is revalidated at compensation time and may refuse. Every other
  record change.
- **NotReversible**: no complete inverse exists. A structural change whose inverse the journal could not
  keep; a record change whose replaced version has since been purged from history.

*And the value is decided before the change runs, not when undo is attempted.* That is the whole point:
a person is told that something cannot be undone while they can still decline it, on the confirmation
card that already exists for exactly this, rather than months later when they ask for it back. For a
record change, the only thing that can later make it `NotReversible` is a history purge, and the
compensation window is never longer than history retention (TR-8).

*Why:* D-7 lets changes happen unasked, and the placement can be confidently wrong (SC/AG below), so
without this a wrong autonomous decision is unrecoverable except by hand. Draft 7 kept every inverse in
the journal, which made the journal's size cap decide whether a delete could be undone, and kept a
second copy of every deleted value beside the engine's. Once the engine versions records (versioning
plan, V-18), the journal keeps references for record changes and nothing else. Old values then have
one copy, no cap decides a record change's reversibility, and "touched since" becomes exact.

*Consequence, stated as a limit rather than discovered as a bug:* this is not time travel. It is valid
only while nothing else has touched the same records, and it refuses as a whole rather than
half-applying.
- **Record changes:** the undo is validated in full before anything is written, and every blocker is
  named. Each record the request changed must still be at the version the request produced, which
  detects any later change, A→B→A included — a content hash could not. And the state the undo would
  leave must be valid: no restored value colliding on a unique column with a record the request did not
  change, no reference to a record that is gone, no deleted record still referred to from outside. The
  check is never the collection's `schemaVersion`, which does not move when a value changes; AG-9 stays
  what it is. Only then is the request replayed, write by write, in exact reverse order (AG-11a,
  AG-11f).
- **Structural changes:** the inverse lives in the change journal, not in the diagnostic payloads
  (AG-11e), which is why TR-2b's payload rules survive for them.

---

## 3. Architecture

### 3.1 Projects

```
TokkDb.Assistant.Storage          the contract: IStorage, definitions, validation.
                                  No engine reference. Testable with a fake.
TokkDb.Assistant.Storage.Engine   the contract implemented on TokkDbConnection.
TokkDb.Assistant.Ingestion        csv / xlsx / docx / txt -> tables and text.
                                  Deterministic. No model, no storage.
TokkDb.Assistant.Trace            the trace model and the recorder interface:
                                  RequestTrace, ExecutionStep, ModelCall, DataChange,
                                  request state. Types and one interface, no behaviour,
                                  no dependencies of its own.
TokkDb.Assistant.Agents           operations, tool scoping, model configuration,
                                  context assembly, the Microsoft Agent Framework
                                  orchestrator, the trace recorder.
TokkDb.Assistant.Diagram          trace -> geometry. Pure layout, no MAUI app types.
TokkDb.Assistant.Application      MAUI: chat, diagram host, detail panels, settings (renamed from TokkDb.Assistant.App).
TokkDb.Assistant.Tests            everything deterministic, xUnit.
TokkDb.Assistant.TokenBudget      console harness: scenarios, measured tokens, report.
```

Dependencies run one way only:

```
Trace           -> nothing
Storage         -> nothing
Ingestion       -> nothing
Agents          -> {Storage, Ingestion, Trace}      never Storage.Engine, never Diagram
Diagram         -> Trace
Storage.Engine  -> {Storage, Trace, TokkDb}
TokenBudget     -> {Trace, Agents, Storage, Storage.Engine}
App             -> {Agents, Diagram, Trace, Storage.Engine}
```

`Agents` depending on the contract rather than the implementation is what makes the
orchestration testable against a fake storage as well as a fake model.

*Why `Trace` is its own project, and why this is a correction rather than a preference.* An
earlier version of this section declared `Agents -> Diagram` and `Diagram -> Agents` at once.
That is not a design weakness, it is a project reference cycle, and .NET refuses to build one.
Extracting the trace model breaks it, and the extraction is justified independently: the trace
has five consumers, not two. The diagram lays it out, the orchestrator writes it, the app renders
its detail panels, the budget harness reads every figure of §6.2a from it, and `Storage.Engine`
persists it. A type five projects need belongs below all five.

The cycle also had a second, quieter error in it: **the orchestrator has no reason to depend on
the diagram at all.** Layout is a presentation concern. `Agents` produces traces, `Diagram`
turns them into geometry, and `App` composes the two. Nothing references `Diagram` except the
application, which is why a change to the drawing can never invalidate an orchestration test.

The recorder is split along the same line: its **interface** lives in `Trace`, so that
`Storage.Engine` can satisfy TR-4 without depending on the orchestrator, and its
**implementation** lives in `Agents`, which is where the unit of work and the operation are.

### 3.2 What is inherited from the engine, free

The engine already provides, and the assistant does not rebuild: atomic transactions and a
journal; B+Tree primary and secondary indexes; an access-path planner with a `QueryReport`
saying what it chose and what it cost; order-preserving key encoding for every scalar
including `Long`, `Decimal`, `DateTime` and `Guid`; lazy schema migration with an explicit
`Rewrite`; a unit-of-work API; the catalogue as documents, with reserved collections that
`Initialize` creates when they are missing.

Since `docs/versioning-requirements-and-plan.md` landed — its Phases 1 to 9 are on this branch — one
more thing: version history for every versioned collection — each record's versions, restoring one, a
diff of two through the current schema with whatever it cannot carry reported, a per-record purge, and
erasure within a stated boundary. The assistant uses it for undo, for a change's before-and-after table
and for erasure (D-17, TR-6, NF-4d). That was an engine plan of its own, not an engine change this plan
made, and its Phase 9 built the assistant's half: the journal's version references, the six operations of
SC-12 on both backends, the compensation as a test-local helper, and erasure through the assistant. Steps
4.7, 9.2 and 9.4 adopt those rather than write them again.

### 3.3 What the old application is for now

`TokkDb.LLM.*` is a reference, not a dependency. Before rebuilding each piece, read the old
one — particularly §2.2 of `docs/requirements-and-plan.md`, which records what the two storage
backends disagreed about and why. The new contract should arrive at those answers on purpose.

---

## 4. Requirements

### 4.1 Storage contract (group SC)

**SC-1 (M).** The assistant shall define its own `IStorage` and its own schema definitions,
with no type from `TokkDb.LLM.*` appearing in any signature.
*AC:* the assistant's projects compile with no reference to any `TokkDb.LLM.*` assembly; a
build-time check or test asserts it.

**SC-2 (M).** A collection definition shall carry only logical properties: name, purpose,
columns, metadata, display rule. No page number, chain pointer, index root or record count
may be reachable from it.
*AC:* a reflection test over the definition type lists exactly the logical members.

**SC-3 (M).** Values shall be written on the way in, not interpreted on the way out. Every
column type is stored as itself.
*AC:* a value of the wrong type for its column is rejected at write with a typed error naming
the column; no write can make a record unreadable. The rejection is about **the field being
written**, not the record: a record still holding an unconverted value in another column (SC-6b)
is writable, and a write that does not touch that column is never refused because of it.

**SC-4 (M).** The contract shall settle, rather than leave open, the questions §2.2 of the
engine plan recorded: record identity is `Ulid`; field validation happens on write; a
duplicate in a unique column is refused with a typed error naming the column and the record
that holds the value; collection names are compared ordinally and trimmed at creation;
`GetAll` promises no order.
*AC:* each is covered by a contract test; the contract's own documentation states the choice.

**SC-5 (M).** A batch/unit-of-work API shall exist from the first version, and an import of
hundreds of records shall use it.
*AC:* an import of 500 records is one commit; an import that throws half way leaves nothing.
This is about **operation** failure, not row failure: a malformed row is rejected before the
transaction opens and does not make the import fail (IN-9, IN-9a).

**SC-6 (M).** Structural change shall be expressible: add a collection, add a column, rename a
column, change a column's type, remove a column, remove a collection, **add a relation, remove a
relation**, with the engine's lazy migration underneath and an explicit converge command exposed.
*AC:* a collection whose column was retyped serves records written on both sides of the change;
the converge command makes them agree and is idempotent; every structural action the assistant
can propose (AG-3) has a matching contract operation.

**SC-6a (M).** Type conversion shall follow one **rule** with a short list of exceptions, rather
than a matrix nobody maintains. A conversion is lossless when every stored value round-trips:
widening is lossless, so `Int32` to `Int64` to `Decimal` and anything to `String` need no
evidence. Every other direction is a change under D-14 whose evidence is the count of stored
values that will not convert, computed before the change is proposed.
*AC:* narrowing a column with 3 values that will not fit reports those 3 and their records; a
widening conversion produces no question.

**SC-6b (M).** A value that cannot convert shall be **kept, flagged and surfaced**, never dropped
and never allowed to make a record unreadable (SC-3). Before convergence a read returns it in its
recorded type with a needs-attention marker; the browser shows it as such; writes to that record
are not blocked, and the converge command reports what it could not convert rather than silently
finishing.
*AC:* a record holding an unconvertible value is still readable, still shows in the table, and is
identifiable as needing attention; converge is idempotent and reports the same set twice.

*Why this had to be stated:* lazy migration upgrades records on read, so an old value that will
not convert has to do *something* on read. SC-3 forbids the two easy answers, which are to throw
and to discard. Keeping the value is the only one left that keeps both promises, and the browser
already shows an absent field rather than hiding it, so the interface precedent exists.

**SC-6c (M).** A column holding values of more than one type shall never produce a **silently
incomplete** result. A filter, a range or a sort on such a column reports how many records were
excluded for not yet being of that type, in the execution info of SC-7a, and the interface shows
it.
*AC:* a range query for cost over 500 on a column where 3 values are still text returns the
matching numbers **and** says 3 records were not considered; the same query with no pending
values reports nothing extra.

*Why the index needs no change at all, which is the part worth knowing.* Every encoded key
carries its type as its first byte (`TokkDb.Documents/Keys/KeyTag.cs`), so a mixed column neither
throws nor slows down: keys sort into runs grouped by tag, and the encoder's own comment records
that comparing across tags is defined and meaningless. What a mixed column actually does is
**exclude** — a range walk over the numeric run never enters the string run — and an answer that
looks complete while omitting records is precisely the failure this document works hardest to
prevent. So the fix belongs on the reporting side, not in storage. Moving unconverted values into
a shadow field would contradict SC-3 and SC-6b and give every reader a second place to look,
permanently, for a state that is temporary.

**SC-6d (M).** Uniqueness in a column holding more than one type is **per type**, and this shall
be stated rather than discovered: the text `500` and the number `500` are two distinct keys.
*AC:* accepting a natural key on a column with pending unconverted values reports them, because
the uniqueness rule IN-6b creates cannot see across types either.

**SC-7 (M).** Reading shall be through one declarative query model (D-16), validated against
the schema before execution, and the result shall carry a **storage-owned `QueryExecutionInfo`**
describing how it was served. The internal model supports projection, filtering, sorting, limits,
cursor paging, aggregation and relation traversal. The model-facing subset is narrower and
separately specified, and C# expands it into the internal form.
*AC:* an invalid query is refused with structured errors naming what did not fit; the browser's
sort and filter (BR-4) and the assistant's retrieval (QR-1) both go through the internal model,
and a feature added to it requires no change to what a model emits.

**SC-7a (M).** `QueryExecutionInfo` shall be part of the contract and shall speak in stable
concepts of its own — index seek, range walk, scan, records examined, records returned, and
**records excluded as not of the column's type** (SC-6c) — which the engine adapter maps
`QueryReport` onto. **Every implementation reports what it actually did.**
A fake with no planner reports a scan, because it performs one.
*AC:* the shared contract suite asserts that execution info is present and internally consistent;
assertions that a particular query becomes an index seek live in the engine-specific tests, not
in the shared suite.

*Why the fake must not pretend:* a fake that returns a "deterministic equivalent" of an index
seek makes the shared suite pass against a claim no code supports, and the suite exists to catch
exactly that. The plan already separates the two by having each implementation supply a factory,
so the split costs nothing.

*Raised from should to must:* it is shared infrastructure for retrieval, the browser, filtering,
sorting, follow-ups and aggregation. Nothing else in the plan is depended on by as many parts.

**SC-8 (M).** A relation shall have a stated **integrity action**, chosen when it is created, from
restrict, cascade, set empty, or require a replacement. **Restrict is the default.** The action
applies when a referenced record is deleted, when the field implementing the relation is removed,
and when the whole thing on either side is removed.
*AC:* deleting a conference that four expenses point at is refused by default, with a message
naming the four in plain words rather than as an integrity error (UI-2); the same with cascade
chosen deletes all five and records each deletion separately; **set empty on a required field is
refused when the relation is created**, not when the first delete discovers it, because the two
rules cannot both hold.

**SC-8a (M).** Cascade shall be an **explicitly confirmed choice**, never a default and never
silent, and each cascaded deletion shall be its own `DataChange` entry.
*AC:* choosing cascade produces a confirmation whose evidence is the count of records that would
go with it; undoing the request afterwards restores every one of them, which is only possible
because each was recorded.

*Why relations needed a requirement at all:* they were traversed (BR-7), classified when added
(D-14, AG-4), and relied on by compensation, which restores a deleted record under its original
identity so that relations survive. Nothing created them and nothing said what a delete does to
them. Every requirement written on top of that assumption inherited the gap.

**SC-9 (M).** Checking many candidate values against an index shall be expressible as **one
ordered pass**, not as many lookups: the contract takes a set of values, and the implementation
encodes them, sorts them into key order and walks the index once.
*AC:* checking 10 000 fingerprints against a collection of 100 000 reads a number of pages
bounded by the pages in the range walked, not by the number of values checked, and a test asserts
the page-read count against that bound.

*An engine assumption to verify rather than inherit:* "each page once" holds only if the B+Tree
can walk leaves without descending through the internal nodes again for every key. Whether it
does is checked in step 1.5 and the answer recorded there; if it does not, the bound is the
weaker one above and the ordered pass is still the right shape, because it turns random reads
into sequential ones either way.

**SC-9a (S).** Where the incoming set is small relative to the collection, per-value seeks remain
the cheaper path, and which is used is decided by ratio the way an access path is.
*AC:* the execution info says which was used.

*Why a batched lookup would not have been the fix.* The engine has no page cache: `ReadPage`
allocates a buffer and reads the file on every call. Ten thousand random index lookups are
therefore ten thousand physical reads multiplied by the tree height, and an API that takes a set
and loops inside itself hides that loop rather than removing it. A B+Tree's answer to "which of
these 10 000 keys exist" is a merge against its own ordering: sequential pages, each touched
once. This is what makes IN-9a's rule that everything is classified before the transaction opens
affordable at NF-5's scale.

**SC-10 (M).** A **conversation** shall be a stored thing with a stated lifecycle: created on
the first turn, appended to per turn, listed by most recent activity, renameable and deletable. A
turn records who spoke, the text, its attachments by reference, and the request it started.
*AC:* every conversation and every turn survives a close and reopen (S-7); deleting a conversation
leaves the data its requests stored and says so, because a conversation is a record of what was
said and not a container for what was kept.

*Why this needed a requirement of its own:* section 1.1 promises conversations in the same file,
S-7 asserts they all come back, UI-1 lists them and TR-4d says the conversation continues — and
nothing defined them. A thing four requirements depend on cannot live only in their prose.

**SC-11 (M).** Every thing stored shall have a **display value**: the one field by which a person
recognises a record. It is derived by a stated rule with a stated fallback and is never absent.
The rule is: the collection's display rule where one is set; otherwise the first required text
field; otherwise the first text field; otherwise the record's identity, rendered short. A display
rule is proposed when the thing is created (AG-3) and can be changed later, which is `Safe` under
D-14 because it changes no stored value.
*AC:* a collection created with no display rule still shows a sensible title in BR-2's table and
BR-5's detail; a collection with no text field at all shows a shortened identity rather than an
empty cell; changing the display rule rewrites no records.

*Why it could not stay a future capability:* EX-1 lists display values among the things that may
be added later, while BR-2 leads its table with one and BR-5 titles the record with one. Two
requirements in the browser already depend on it, so it exists now and EX-1 covers only its
refinement.

**SC-12 (M).** The contract shall offer the version operations undo needs, and nothing more:
- `HeadVersion` — the version a record is at;
- `Keeps` — whether a version is still kept;
- `DiffVersions` — column by column, old beside new, through the thing's current shape, **carrying
  what that shape cannot show** rather than dropping it: a value in a field since removed, or one that a
  since-changed kind cannot take, comes back named as such (TR-6a);
- `RestoreVersion`;
- `PurgeRecordHistory`;
- `Erase`.

Both implementations provide them, and one contract suite checks both.
*AC:* the suite passes against `TokkDbStorage` and `MemoryStorage`. Inside a unit of work, `HeadVersion`
after an update returns the version the update produced, and after a delete the tombstone. After a field
is removed, the diff of two versions from before the removal still lists that field's change, marked as
since removed, on both backends. The contract project still references nothing.

*As built, and not yet true of the diff:* the engine reports every value the current schema cannot show
(versioning plan, V-10, G-6), but `TokkDbStorage.DiffVersions` reads each version through `GetAsOf` and
discards that report, so a removed field's change vanishes from the table and a lossy retype's old value
with it. That is the one place the assistant made the engine's history silently lossy. TR-6a is the
requirement, and step 7.3 closes it before the detail panel is built on the diff.

*Why both, and why only these:* orchestration is tested against the in-memory storage (D-12), so undo
has to mean the same thing there. A full copy per version in memory changes the cost, not the meaning.
Browsing history and reading a record as of a moment stay out of scope (§1.2), so the contract grows by
what compensation, the trace's before-and-after table and erasure use, and no further.

**SC-12a (M).** Every collection the assistant writes to shall be versioned. A collection it creates is
created versioned. Opening a storage switches any user collection that is not versioned on, without
rewriting a record.
*AC:* a collection created through the assistant is versioned whatever the engine's default. A database
holding an unversioned collection is switched on at open, and its records gain history only when they
are next changed.

*Why not leave it to the engine's default:* the engine decides its default on measurement (versioning
plan, V-13), and the assistant's undo cannot depend on how that turns out. A collection the assistant
writes to but cannot undo would need draft 7's second mechanism back.

### 4.2 Ingestion (group IN)

**IN-1 (M).** `.csv` and `.xlsx` shall be parsed in C#, with no model involved in parsing:
delimiter and encoding detection, header detection, per-column type inference, per-column
profile (distinct count, null count, min and max, a few examples), multi-sheet support.
*AC:* a file with a two-row preamble before the header, mixed European and invariant decimal
separators, and a column that is 98% integers and 2% text is parsed with the header found and
the mixed column reported as text with the exceptions listed.

**IN-1a (M).** Inference shall produce a type, a confidence, its **evidence as counts with
examples**, and any ambiguity — not a type alone. Evidence is: how many values parsed as each
candidate, how many as none, and the failures quoted.
*AC:* a column of `007`, `013`, `142` reports text with the leading zeros as the evidence; a
date column where every day is twelve or under reports the day-month against month-day
ambiguity explicitly rather than choosing.

**IN-1b (M).** Where candidates are close, the **wider** type wins. Text is preferred to a
number when either would parse.
*AC:* `007` is stored as text, not as 7.

*Why the asymmetry is a rule and not a preference:* storing `007` as text loses nothing and can
be narrowed later by a retype, which the engine performs lazily. Storing it as 7 is
irreversible. The migration machinery only runs one way without loss, so inference should leave
the recoverable option open and let a confirmed retype close it.

**IN-2 (M).** `.docx` and `.txt` shall be converted to plain text, preserving paragraph and
list structure and table content in a form a model can read.
*AC:* a `.docx` containing headings, a bulleted list and a two-column table converts to text
in which all three are still distinguishable.

**IN-3 (M).** What reaches a model shall be a *rendering* chosen for the job, never the raw
file: for tables, a compact column profile plus a bounded sample of rows; for prose, text
chunked to a budget.
*AC:* a 50 000-row spreadsheet produces a model input of a bounded size that does not grow
with row count; the bound is asserted by a test.

**IN-4 (M).** Ingestion shall be resumable in the face of a bad row. Rows that cannot be
stored are reported with their line number and reason, and the rest are stored.
*AC:* a CSV with 100 rows of which 3 are malformed stores 97 and reports 3, in one batch.

**IN-5 (M).** Two questions shall be kept apart and answered separately: *is this the same kind
of thing* (schema matching, AG-3) and *have I seen this row before* (record identity). Matching
structure shall never be taken as evidence that records are the same.
*AC:* importing the same spreadsheet twice adds its records to the same thing **and stores no
row twice**.

*Why the criterion is worded so plainly:* an earlier wording of it was satisfied by storing 44
conferences from importing 22 twice. The plain version is the one that excludes that.

**IN-6 (M).** Record identity shall rest on a natural key where one exists. A candidate key is
proposed by the model and **confirmed deterministically in C#** by checking that the column is
unique and non-null **across the incoming data and against the records already stored**. Where
no key exists, a fingerprint over normalised values is the fallback.
*AC:* a file with a DOI column has it proposed and accepted as the key; a file whose candidate
column repeats a value has it rejected without the user being asked; a candidate that is unique
in the file but collides with stored records is rejected the same way and for the same reason;
the fallback catches an exact re-import and is documented as not catching the same record with
one value corrected.

*Why checking the file alone was not enough:* a key unique in one spreadsheet says nothing about
the twelve hundred records already stored, and IN-7's merge policy leans on a uniqueness rule
that nothing in the document created. Both halves of that are fixed here and in IN-6b.

**IN-6a (M).** The fingerprint shall be a **stored, indexed field**, not a value computed during
the import, and it shall record the **normalisation version** it was computed under.
*AC:* an import's whole set is checked in **one ordered pass** over the index (SC-9), and a
single row arriving outside an import is an index lookup rather than a scan (SC-9a); a change to
normalisation does not silently stop matching, because the version differs and the mismatch is
reported rather than read as "not a duplicate".

**IN-6b (M).** Accepting a natural key shall **create or verify the uniqueness rule** that makes
it one, and that creation is a structural action inside the placement proposal (AG-3), classified
under D-14 with the existing collisions as its evidence.
*AC:* accepting a key on a collection with two colliding records reports those two records rather
than failing at the first write; no import relies on a uniqueness rule that does not exist.

**IN-7 (M).** The merge policy shall be explicit per import — add all, skip existing, update
existing, or ask — with **one default, not two: skip existing**, on the natural key where one
exists and on the fingerprint of IN-6 where none does. *Add all* is an explicit choice, never
what happens by omission. **Update existing requires a natural key**: a fingerprint covers every
normalised value, so a record that changed has a different fingerprint and can never be matched
for update. Without a key the policy degrades to skip or add, and says which. What happened shall
be reported per row under IN-8.
*AC:* a second import of the same file reports "kept 21, skipped 1 that matched on DOI" rather
than reporting a count alone; choosing update existing on an import with no key is refused with
that reason rather than silently behaving as add all. The engine's unique index and its typed
violation, which already names the conflicting record, are what implement this; no new storage
machinery is added.

*Why the previous default was a contradiction and not a gap:* it read "add all when no key
exists", while IN-5 requires that importing the same spreadsheet twice stores no row twice. A
file with no natural key satisfied one requirement only by breaking the other. The fingerprint was
already named in IN-6 as the fallback; the default simply has to use it.

**IN-8 (M).** Every incoming row shall carry a **disposition**, and the disposition shall be two
things rather than one. An **outcome** from a small closed set: `Inserted`, `Updated`, `Skipped`,
`Rejected`. And a **reason** carrying the detail: duplicate on the key, naming the key; unique
violation, naming the column and the conflicting record; validation failure, naming the column
and the value; unparsable, naming the line. "Conflicted" is not a fifth outcome — it is
`Rejected` with a unique violation, or `Skipped` awaiting a decision under IN-7's *ask* policy.
*AC:* an import reports counts by outcome with examples, in the shape IN-1a already uses for
evidence; a row skipped as a duplicate and a row rejected for an unparseable date are
distinguishable in the report and in storage; no reason is reachable only as free text.

*Why two axes and not one:* an outcome that names its reason, such as "skipped duplicate", forces
every new reason to become a new outcome, while an outcome with no reason, such as "conflicted",
cannot be acted on. Separating them keeps the set the interface renders small and the set the
system can explain open.

**IN-8a (M).** The disposition shall be **persisted with the change record**, not only shown.
*AC:* undoing an import (D-17, AG-11) deletes what it inserted, restores what it updated and
does nothing to what it skipped; a test shows that an undo computed from a stored count alone
deletes records the import did not create, which is why the count is not enough.

**IN-9 (M).** Row failure and operation failure shall be **different things**, in three classes.
**Row rejections** are found before the transaction opens, kept in the report, and do not stop the
valid rows committing together. **Write conflicts** are expected and handled by the merge policy
of IN-7 before the commit. **Fatal failures** — a fault, a cancellation, a broken invariant —
roll the whole import back.
*AC:* a file of 100 rows with 3 unparseable commits 97 and reports 3 (IN-4); an import cancelled
half way leaves nothing (SC-5); and the two are distinguishable in the report rather than both
reading as "some rows did not make it".

**IN-9a (M).** All row-level classification shall happen **before the write transaction opens**,
without the "where practical" that would make it optional.
*AC:* the transaction contains only rows already known to be valid, so the only things that can
fail inside it are genuine faults, and SC-5's all-or-nothing holds with no exception clause.

*Why the strong version is affordable here:* IN-1 already parses and profiles the whole file
before anything is written, so nothing is gained by deferring validation, and AG-10 serialises
writes, so the one conflict that normally cannot be predicted before a transaction — a uniqueness
violation created by someone else mid-import — cannot arise. An existing requirement is what
makes the strict rule cheap.

*And SC-9 is what keeps it cheap at NF-5's scale.* Classifying every row before the transaction
means asking the index about every row, which done one row at a time is ten thousand random reads
against an engine with no page cache. One ordered pass over the index makes the same question
sequential, so the rule costs a sort rather than a stall.

### 4.3 Agents and orchestration (group AG)

**AG-1 (M).** Every request shall be handled as a named **operation** with a declared model
configuration, a declared tool subset, and a declared context budget. No model call may be
made outside an operation.
*AC:* a test enumerates the operations and asserts each declares all three; an attempt to call
a model without an operation fails.

**AG-1a (M).** The context budget shall be **enforced before the call**, not documented. The
assembler checks the assembled context against the operation's budget and trims deterministically
or fails; the model's maximum context is a capability limit and never the target.
*AC:* an operation that would exceed its budget fails a test rather than degrading quietly; no
budget is set from taste, each is set from R-3's measurement.

**AG-1b (M).** Every scenario shall declare a **maximum number of model calls** and a
**maximum end-to-end latency**, and both shall be asserted.
*AC:* a scenario that gains a model call fails until the limit is raised deliberately.

**AG-1c (M).** A scenario's end-to-end success rate shall be **measured over repeated runs**,
and the measured figure, not a predicted one, shall be the gate.
*AC:* a scenario whose measured rate falls below its stated floor fails; the harness states how
many runs are behind the figure and asserts against the **lower bound of the interval** rather
than the point estimate, so a thin sample fails as thin instead of passing as good.

**AG-1d (M).** Per-call success rate shall also be measured, and the product of the per-call
rates reported as a **diagnostic beside the measured figure, never as a substitute for it**. The
per-call rate that composes is the one **after** AG-5's repair, because a repaired call is not a
failed request.
*AC:* the harness reports the measured rate, the predicted one and the distance between them.

*Why the distance is the interesting number:* the product assumes failures are independent, and
here they demonstrably are not. Context shift is a property of the request rather than of the
call, so when it happens every call in that request is likelier to fail and the failures clump in
the same runs. Positively correlated failures make the product **understate** end-to-end success:
conservative, still wrong, and wrong in the direction that blocks a split which would have been
fine. So the distance between measured and predicted is the correlation measurement, and a wide
gap says the failures share a cause, which is a lead rather than a number.

**AG-1e (S).** Failures shall be **classified by mode** — malformed output repaired, malformed
output past the bound, empty reply, timeout, refusal — and counted per mode.
*AC:* the harness distinguishes a repaired malformed output from an empty reply under context
pressure, which is what separates correlated failures from independent ones without any
statistics.

*What none of this changes:* splitting work across calls still multiplies failure, and that is
still the reason to measure it. The measured rate on the spike's mapping call was 14 of 20, taken
with the output cap broken (R-2b). Where a call can be removed without widening a schema, it
should be: outcome sentences can be templated in C# for the common cases, and intent
classification can be skipped deterministically when the signal is unambiguous, such as an
attachment that parsed as a table with no question in the message.

**AG-1f (M).** "Outside an operation" shall be made **impossible rather than forbidden**. The
chat client type is `internal` to the orchestration assembly, visible to the test project only;
the single public entry point takes an operation and performs the context check and the trace
recording itself; and the operation runner is the **only** place the client is resolved from the
container.
*AC:* an architecture test asserts that no type outside the orchestration assembly references the
chat client type, so a public wrapper cannot reopen the hole; a second asserts the container hands
the client to nothing else, since `internal` alone does not stop a composition root from handing
it out.

*Why a convention would not have held:* AG-1's own criterion says an attempt to call a model
without an operation fails, and nothing in an enumeration test makes that true of code written
next year. The boundary is what makes the criterion mean something.

**AG-2 (M).** The user shall not have to say what they want done. Intent is determined by the
system from the message and its attachments.
*AC:* "here are last year's conferences" plus an `.xlsx` is handled as storing; "how many
conferences last year" is handled as retrieval; neither message names an operation.

**AG-3 (M).** Where data belongs shall be decided by the system, and the decision shall be one
object: a **placement proposal** carrying the target (an existing thing, or a new one with a
proposed name), a field mapping per incoming field (with its kind: exact, renamed or coerced,
and the type on each side), the incoming fields that map to nothing and what is proposed for
each, the existing fields nothing fills, the structural actions implied and each one's class
under D-14, a confidence figure with its evidence, and one sentence of rationale.
*AC:* with a `Conference` thing already present, a spreadsheet of conferences whose cost column
is called `amount_eur` proposes a mapping of `amount_eur` onto `Cost` rather than a second
field; a spreadsheet of expenses proposes a new thing; and both proposals are one object that
validation, the confirmation card and the trace all read.

**AG-3a (M).** Confidence shall be computed in C# from observable evidence, never taken from
the model. The evidence is at least: name overlap after normalisation, per-field type
compatibility, the count of unmapped incoming fields, the count of unfilled required fields,
and whether a natural key matched.
*AC:* a model that reports a confidence of its own has it ignored; the spike's own mapping
model returned `"confidence": 0` on a correct answer, and a test pins that this value is not
what the gate reads.

**AG-3b (M).** A proposal shall apply without asking only when the best candidate clears a
floor **and** its margin over the runner-up clears a gap. Otherwise the user is asked, with the
candidates shown. Proposing a **new** thing shall clear a higher bar than adding to an existing
one.
*AC:* two candidates scoring within the gap of each other produce a question even when both
score highly; a single candidate above the floor with no close rival applies silently.

**AG-3c (S).** The model shall rank and correct candidates produced in C#, rather than name a
target freely.
*AC:* the placement call receives a shortlist and returns a choice from it or an explicit "none
of these"; it cannot return a name that was not offered.

**AG-3d (M).** A proposal shall be **immutable once validated**, and validation shall produce a
distinct type rather than set a flag on the same one: an unvalidated candidate cannot be passed
to execution, because it does not compile. The validated proposal carries a content hash; the
confirmation card is rendered from it, and the executor re-checks the hash before applying.
*AC:* a validated proposal exposes no mutable member, asserted by reflection; substituting a
different proposal between the confirmation and the write fails the hash check and the write is
refused, rather than applying something the user was never shown.

*Why a hash and not a proposal lifecycle:* D-15 already holds the request's states, and a second
state machine over the proposal would record awaiting-user, approved and applied as duplicates
of `WaitingForUser`, `Resuming` and `Completed`. Two state machines that have to agree are the
failure this is meant to prevent. The hash does the different job of making AG-8's promise
falsifiable: without it, "executes exactly what was shown" is a property no test can break. And
what legitimately changes between the question and the write is not the proposal but the world,
which is AG-9 for the schema and AG-11a for the records.

**AG-4 (M).** Change shall be classified under D-14 and handled by class: `Safe` applies
silently, `ReviewRequired` and `Destructive` ask. Every question carries the counts and
examples that make it answerable, computed before the question is put.
*AC:* adding an optional field produces no prompt; adding a *required* one reports how many
records would become incomplete; adding a *unique* one reports how many existing values
collide; adding a relation reports how many source values match nothing; removing a field
reports how many records hold a value for it. Each requires a yes, and none is phrased in
schema terms.

**AG-5 (M).** Structured model output shall be validated, and a model that emits something
malformed shall be repaired within a bounded number of retries before the operation fails with
a message the user can act on.
*AC:* a fake model that returns invalid JSON twice and valid JSON on the third attempt
completes; one that never succeeds fails with a stated reason, and both are visible in the
trace.

**AG-6 (M).** The conversation context shall be bounded: a sliding window of recent turns plus
a rolling summary of older ones, regenerated on a stated trigger.
*AC:* in a 200-turn conversation the assembled context stays within budget and a constraint
stated in turn 3 is still honoured in turn 150.

**AG-6a (M).** The assembled prefix of an operation — system instructions, schema block, tool
block, in that order — shall be **byte-identical between calls of that operation**, and this shall
be asserted rather than intended.
*AC:* a test hashes the prefix of two calls of the same operation with different user input and
fails when they differ; the assertion covers ordering, whitespace and the serialisation of the
tool block.

**AG-7 (S).** Model configuration shall be per operation: provider, model, context size,
temperature, with a default and per-operation overrides.
*AC:* extraction and mapping can be pointed at different models without code change.

**AG-8 (M).** A request shall carry a persisted state under D-15, and shall be resumable after
a restart. `WaitingForUser` holds the validated, resolved intent, never the conversation.
*AC:* the application is killed while a confirmation is on screen; on reopening, the question is
still there and answering it executes exactly what was shown, with no further model call, and
the content hash of AG-3d is what proves it was the same proposal.

**AG-8a (M).** The state machine shall be **idempotent and recoverable**, not merely persisted.
Three parts. The database is opened under a **single-writer lock**, since D-11's one file makes a
second instance possible and nothing else prevents it. Every transition is a **compare-and-swap**
on the request's transition counter, so a confirmation submitted twice moves the request once.
And the **idempotency key of a resolved action is the content hash of AG-3d**, so an action that
already committed is recognised rather than repeated.
*AC:* opening the same database twice reports the file is in use rather than corrupting it; a
double-submitted confirmation produces one write, asserted by a test that fires the transition
concurrently; replaying a resolved action whose hash is already recorded as committed is a no-op.

**AG-8b (M).** Startup recovery shall be **defined rather than discovered**. A request found in
`Running` cannot be resumed, because the model call it was inside is gone: it is marked `Failed`
with a reason of interrupted, unless its `DataChange` shows the mutation committed, in which case
it is completed from the record. A request in `WaitingForUser` is restored as it was (AG-8). A
request in `Resuming` is claimed atomically before any work is redone.
*AC:* each of the three is a test with the process killed at that point; no recovery path leaves a
request in a state from which nothing can act on it, and none re-executes a committed write.

*Why the lock rather than a lease:* writes already serialise inside one process (AG-10), so the
only genuine contention is between processes, and that is a file-level concern with a
file-level answer. A per-request lease would be machinery for a problem the single-writer rule
removes outright.

**AG-9 (M).** A proposal shall record the `schemaVersion` it was computed against, and the write
shall re-check it. A mismatch re-plans rather than applying.
*AC:* a schema change committed between a proposal and its write causes a re-plan, not a blind
apply or a silent failure.

**AG-10 (M).** Write operations shall serialise; reads shall not block on them. The interface
reports busy rather than failing.
*AC:* a delete from the browser during a long import waits and then succeeds; the browser
continues to read throughout.

**AG-11 (M).** A request that changed data shall be compensable under D-17, at request
granularity, and shall run inside **one unit of work** (SC-5), so that nothing half-applies by
construction rather than by care.
*AC:* "undo that import" removes exactly what the import added; a compensation that fails part
way leaves storage as it was, which is rollback and not cleanup.

**AG-11a (M).** A compensation of record changes shall run in **two passes inside one unit of work**, and
the first shall find **every blocker before anything is written**.
1. **Validate.** For each record the request changed, collect every blocker:
   - **touched since:** its head is no longer the `VersionId` of the request's last change to it;
   - **the end state conflicts.** The end state is the record's values in the version it would be restored
     to, computed from its current values and `DiffVersions` (SC-12). It conflicts when a restored value
     collides on a unique column with a record the request did not change; when a reference it would hold
     points at a record that is gone; or when the record would be deleted and a record the request did
     not change still refers to it.

   If anything blocks, the undo is refused whole, naming every blocker (AG-11b), and nothing has been
   written.
2. **Replay.** Undo each record change in exact reverse order (AG-11f) by restoring its `PreviousVersionId`
   — or deleting the record when that is null, for an insert — without re-checking versions the
   compensation itself produces. A constraint refusal during the replay aborts the whole unit of work and
   names the record.

The collection's `schemaVersion` is a different check for a different purpose (AG-9) and cannot stand in
for either pass.
*AC:*
- undoing an import after an unrelated record has changed still works;
- undoing it after one of *those* records has changed refuses and says why;
- a record changed A→B→A by a later request counts as touched, which a content hash could not detect;
- a request that updated one record twice is undone, although the compensation's first restore creates a
  new version;
- an undo blocked by two records — one touched since, one whose restored unique value another record has
  taken — is refused before any write, naming both;
- the replay limit below is a test: refused atomically, naming the record, storage unchanged.

*Why the version check is made once and not per step:* a restore creates a new version, so checking each
write as it is replayed fails at a record's second-to-last change. Nothing outside the compensation can
change a record between the validation and the replay, because both run inside one unit of work under the
single writer (AG-10).

*The limit, stated rather than discovered:* validation checks the state the undo would *end* in, while the
replay passes through intermediate states. So an undo can still be refused at replay although its end
state is valid. For example, a record went a→b→c inside the request, and another record has since taken b.
The refusal is atomic and names the record. Removing the limit needs deferred constraint checks in the
engine (versioning plan, F-11), and this plan does not wait for them.

**AG-11b (M).** Refusal shall be whole, and shall **name every record that blocks it**, with the reason —
touched since, or which conflict — all found by AG-11a's validation before anything was written. Undoing
the rest is offered as a second, explicitly chosen action, never as the silent outcome of the first.
The rest is validated as an undo of its own before it is offered: leaving a record out changes the state
the undo ends in, and can create a blocker the whole undo did not have. If the rest has blockers of its
own, they are named with the offer. Taking the offer is a **confirmation of its own**, in the shape of
UI-4: a card stating what will be put back and what will stay as it is, each as a count with the records
named, answered from `WaitingForUser` (D-15) so that what runs is what was shown (AG-3d). It is never
one click on the refusal, and never what happens when the refusal is dismissed.
*AC:* an import of 500 rows of which one has since been edited refuses, names that row, and offers the
remaining 499; accepting that offer is a second answer on a card of its own, which compensates 499 and
reports the result as partial, naming what was left; a partial compensation with no such answer recorded
in the trace fails a test. When leaving one record out would make another's restore collide on a unique
column, the offer names that collision instead of offering a partial undo that would fail.

*Why the partial exists at all:* refusing to undo 499 records because one changed is a defensible
default and an indefensible only option. The rule worth keeping is that nothing partial happens
**silently**, not that nothing partial can happen. And two steps because the second question is a
different question: the first was "undo this request", and the answer was no; the second is "undo these
499 and leave that one", which nobody has yet been asked.

**AG-11c (M).** Every operation shall carry a **reversibility** of `Reversible`,
`ReversibleWithConditions` or `NotReversible` under D-17, computed **before the operation runs**
and recorded with the change.
- **A record insert, update or delete** is `Reversible` when its collection has no unique column and
  takes part in no relation in either direction, and `ReversibleWithConditions` otherwise. Putting a
  record back — or removing an inserted one — can collide with a value another record has taken since,
  or with a relation, which is D-17's third case. It does not depend on the width of the record, on a
  payload cap, or on whether a cascade was involved.
- **A structural change** is reversible only while a complete inverse is retained in the journal and
  still valid; otherwise it is one of the other two.

*AC:* a test enumerates every operation and asserts each declares a reversibility. Deleting a record
wider than any journal cap is `ReversibleWithConditions`, not `NotReversible`. D-17's three
counterexamples refuse whole and name the blocking record. Removing a field from a collection whose
values exceed the journal's cap is `NotReversible` before it runs, not on the attempt to undo it.
Retyping is never `Reversible` in a narrowing direction.

**AG-11d (M).** `NotReversible` shall be stated **in the confirmation, before the change**, in
the words of UI-4 and with the same counts and examples.
*AC:* the card for such a change says it cannot be undone; a change classified `NotReversible`
with no confirmation shown fails a test, because there is no path by which an irreversible loss
happens unannounced.

**AG-11e (M).** What an undo needs shall live somewhere durable, never in the diagnostic payloads of
TR-2b, which are capped and prunable. For a record change, that is the engine's version history,
referenced from the change journal by version identifiers. For a structural change, it is the
**change journal** of TR-8, size-governed.
*AC:* a diagnostics purge leaves every undo inside the compensation window possible. A journal at its
cap refuses a new structural inverse by classifying the operation, rather than by dropping data. A
history purge that removes a version makes the change it served `NotReversible`, and the undo says the
version is no longer kept.

**AG-11f (M).** A compensation covering more than one change shall be replayed in the **reverse of
the recorded order**, and the **whole** of it shall be validated inside the unit of work before any of
it applies. Every change is replayed at its own position, record changes as restores (AG-11a) and
structural changes from the journal. None is merged with another change to the same record.
*AC:* undoing a cascade deletion of a conference and its four expenses restores the conference before
the expenses that point at it, without a topological sort. A request that changed a unique column as X
a→b, then Y c→a, then X b→c is undone. A restore that would violate a rule introduced since is refused
whole, leaving storage untouched.

*Why reverse order is enough, and a topological sort is not needed:* the changes were performed in an
order the same constraints accepted, and change records within a request are ordered, so replaying them
backwards is valid by construction. Sorting is what you need when the order was not recorded, and it
was.

*Why a record's changes are not merged into one restore:* they can interleave with other records' changes
that depend on them. In the X, Y, X case above, restoring X once — at either its first or its last
position — collides with the value Y holds at that moment. Exact reverse order releases each value
before the change that took it is undone. The case ordering cannot save is a uniqueness rule or relation created
**after** the deletion, which invalidates the restore however it is sequenced; that is AG-9's
check, and it is why any cascade-enabled request is `ReversibleWithConditions` and never
`Reversible` under AG-11c. The engine has no deferred constraint checking, so ordering inside
the transaction is the only instrument available and the dry run is the only way to be whole.

### 4.4 Query and retrieval (group QR)

**QR-1 (M).** A retrieval request shall become a structured query, validated in C# against the
schema, and executed through `IStorage`.
*AC:* a query naming a column that does not exist is refused before execution with an error
naming the column.

**QR-2 (M).** Results shall be rendered by the application and shall not be returned to the
model by default.
*AC:* asking for a thousand records costs the same model tokens as asking for one; asserted by
the budget harness.

**QR-3 (M).** A follow-up about results already shown shall be answered in three layers,
cheapest first. **One**: C# answers from result metadata with no model call where it can
recognise the question (a count, a maximum, a range). **Two**: the model emits a *refinement of
the previous query*, which C# runs against storage. **Three**: a bounded digest, as material for
phrasing only.
*AC:* "how many of those" costs zero model tokens; "which of those were in Lviv" is answered by
a derived query rather than failing, which the digest alone could not do; in no layer do the
rows re-enter the model's context.

*The layers do not share a freshness rule, and stating one rule for all three would make the
first wrong.* Layer one answers from the metadata captured when the results were shown, so it
answers **about what was shown** and says so: "of the 21 you were shown". Layers two and three
re-run and answer about current data (QR-3b). Both are defensible; only silence about which is
in force is not.

**QR-3a (M).** A result shall be addressable by a `QueryResultHandle` that carries the query
that produced it, the result metadata (count, the fields present, ranges), and the **ordered
identities of the page that was displayed** — not the rows.
*AC:* a follow-up two turns later still resolves; the handle survives a restart with the
conversation; the identities are bounded by the page shown and never by the size of the result,
and no identity reaches a model, which leaves D-6 intact.

**QR-3b (M).** A handle **re-runs** rather than pinning a snapshot, so a follow-up sees current
data.
*AC:* a record changed between the question and the follow-up is reflected in the answer, and
this is stated in the interface rather than left to be discovered.

**QR-4 (S).** The user shall be able to correct or remove what is shown, by saying so.
*AC:* "the second one should be 2024" updates that record, shows the before and after in the
trace, and does not require the user to name the collection or the record.

**QR-4a (M).** Where a **positional** reference is accepted at all, it shall resolve against the
identities recorded in the handle (QR-3a), never against a re-run.
*AC:* a record inserted or deleted between the answer and the follow-up does not change which
record "the second one" means; a referenced record that has since been deleted produces a message
saying so, rather than silently correcting a different one.

*Why this requirement exists only because of QR-3b:* re-running is the right default for a
question about data and the wrong one for a reference to a row on screen. Ordinal reference is
the single place where the two collide, and it is a correction the user believes they have made.

**QR-4b (M).** A correction reached by position shall be checked for staleness **at the field
being corrected**, not at the whole record and not against the re-run. If that field has changed
since the results were shown, the correction stops and shows the value as displayed beside the
value as it is now. A change to any other field does not stop it, and a record that has left the
query's filter does not stop it either, because the handle and not the re-run is the authority on
what the user pointed at.
*AC:* correcting the year on a record whose unrelated note was edited meanwhile succeeds;
correcting the year on a record whose year was edited meanwhile stops and shows both values; a
record that no longer matches the filter is still correctable by position.

*Why not a per-page version counter:* the handle is immutable and identified, and a new query
produces a new handle, so it already carries a version without a second counter to keep in step.
That is the same reasoning as AG-3d, where validation produces a new type rather than setting a
flag on an old one.

### 4.5 Trace and diagram (group TR)

**TR-1 (M).** Every request shall produce a trace: an ordered set of steps and the edges
between them, with the first step being the user and the second the orchestrator.
*AC:* a request that ingests a file produces a trace whose steps include intent, extraction or
profiling, mapping, the structural change if any, and the write, connected in the order they
happened.

**TR-2 (M).** Diagnostics and change history are **separate models with separate retention**,
even where they share system collections. `RequestTrace`, `ExecutionStep` and `ModelCall` are
diagnostics: larger, prunable. `DataChange` is the audit record: **bounded**, durable, and never
removed by trace retention. For a record change it is a fixed-size set of references — the request,
the record and two version identifiers — because the old values live in the engine's version history
(D-17). For a structural change, bounded means governed by a stated rule rather than by what happened
to be convenient (TR-2b, AG-11e). They are joined by one id.
*AC:* purging traces leaves every `DataChange` intact; the interface renders "the diagram for
this change is no longer kept" rather than failing on the dangling id.

**TR-2a (M).** Each step records its operation name, start and end time, status, and its inputs
and outputs. A data-changing step records what changed.
*AC:* clicking a write step shows what changed, per field, old beside new.

**TR-2b (M).** What a change records is shaped by the **operation**, then bounded by size.
- **A record insert, update or delete** records the record, the version it replaced (none for an
  insert) and the version it produced, and **no field values**. The values are in the engine's version
  history, where the undo reads them and where the before-and-after table is computed (TR-6). A version
  answers "has this been touched since" exactly (AG-11a).
- **A structural change** records its inverse, where one can be kept: small values inline, large ones as
  size, hash and a bounded preview, with a cap per step as well as per value.

*AC:* importing 10 000 records produces change records of fixed size, whatever the width of the rows.
Deleting a record wider than any cap still leaves it restorable (D-17). A single 2 MB value in a record
never enters the journal. A structural change past the cap is classified as below.

*Why the operation matters more than the size:* draft 7 made a delete record the whole record, because
the journal was the only remaining copy, and a purely size-based rule would have truncated exactly that
payload. With versions, a deleted record's last state is kept by the engine, so no record change's
payload has to be large. The rule survives where the journal is still the only copy: structural changes.

*And where even that is not enough, the operation is declared irreversible instead of truncated.*
Removing a field from a wide collection needs every removed value; past the journal's cap that
inverse is not kept, the operation is classified `NotReversible` before it runs, and the
confirmation says so (AG-11c, AG-11d). Silently storing part of an inverse would be worse than
storing none, because it would make an undo that half-restores look like one that worked.

**TR-3 (M).** A model call shall record the model, the token counts in and out, the duration
and a hash of the prompt — not the prompt text.
*AC:* a trace of a request that made four model calls reports four token counts and no user
content beyond what the payloads already carry.

**TR-4 (M).** The durable `DataChange` record shall be written in the **same transaction as
the data mutation it describes**. Neither can exist without the other.
*AC:* an injected failure between the data write and the change write leaves neither.

**TR-4a (M).** Request lifecycle and diagnostic steps shall be persisted **independently, as
they occur**, and linked to the change record by the request id. They are not inside the
transaction of a mutation that may not have happened yet.
*AC:* a request killed while waiting for a confirmation has its state and its steps so far on
disk; a request that made three model calls and then failed has three model-call steps recorded.

*Why this had to be split, and it is a correction.* The previous single requirement put every
trace step in the transaction of the change it describes. That cannot hold with the rest of the
document: TR-5 requires the diagram to grow **while the request is running**, and D-15 requires
`WaitingForUser` to survive a kill — both of which happen before any data write exists, and
neither of which can sit inside a transaction held open across a model call and a human pause.
The split is the one TR-2 already drew for retention, applied to durability as well: the audit
record is atomic with the data, the diagnostics are live.

**TR-4b (M).** A step shall record a **status**, and a step interrupted by a crash shall never
read as completed. Startup reconciliation marks steps left running as `Interrupted`, and marks
the request under D-15's recovery rule (AG-8a).
*AC:* the process is killed mid-step; on reopening, that step is shown as interrupted rather than
as done, and the diagram says so rather than implying a result that was never produced.

**TR-4c (S).** The durability point shall be **stated rather than assumed**, because persisting
each step on its own is a commit per step and the journal makes commits real work. Lifecycle
transitions are durable at every transition. Diagnostic steps are durable within a bounded delay
and the last one may be lost to a crash.
*AC:* the bound is a configured value with a default, and a test asserts that a lifecycle
transition is on disk before the call that follows it begins.

**TR-4d (M).** Everything above shall be readable after the application is closed and reopened.
*AC:* the application is closed mid-conversation and reopened; every earlier request's diagram
is there and the conversation continues.

**TR-5 (M).** The diagram shall be drawn as a sequence of participants and calls, grow while
the request is running, and have clickable blocks.
*AC:* a running request shows steps appearing as they complete; each block opens a detail
panel.

**TR-5a (M).** Drawing and hit testing shall derive from the **same transform**, produced from
the view's scale at draw time, and the diagram shall invalidate and redraw on a display or scale
change. Layout coordinates are device-independent points, never fractions of the viewport.
*AC:* applying scale factors of 1.0, 1.5 and 2.0 to a fixed layout returns the same block for the
same logical point, asserted in the test suite with no second display present; moving the window
between monitors of different scale leaves clicks landing on the blocks they are over.

*Why not normalised coordinates:* text has an absolute size and does not scale with its
container, so a layout expressed as fractions puts labels outside the boxes drawn for them at
every size but the one it was tuned at. The alignment problem is that two code paths compute
coordinates separately, and the fix is to have one path.

**TR-6 (M).** The detail panel shall show JSON by default and a purpose-built view where one
helps: a before-and-after table for data changes, a token and timing panel for model calls,
the structured query and row count for retrieval. A record change's table is computed with
`DiffVersions` from the two versions its `DataChange` names (SC-12).
*AC:* the three specialised views exist; everything else falls back to JSON. After a record change's
versions are purged, its table says "the values of this change are no longer kept", while the request
and the time are still shown.

**TR-6a (M).** A trace shall be shown **as it was, and say what has changed since**. A field a step
named, which the storage has since removed, renamed or retyped, is still shown under the name it had
then, with a plain note of what became of it: since removed, now called something else, or now keeping a
different kind of value. Nothing is omitted and nothing is quietly presented under the current shape. A
record change's table takes this from `DiffVersions`, which names what the current shape cannot carry
(SC-12); a structural step takes it from its own journal payload, which recorded the field as it was; a
reply in the conversation is the record of what was said and is never rewritten. Whether the storage has
changed since a request is known without a search: the proposal recorded the `schemaVersion` it ran
against (AG-9), and a different one now means something changed.
*AC:* after a field is removed, the diagram of an earlier request that filled it still shows the field
and its values, marked as since removed; after a rename, the same table shows the values under the new
name and says what the field was called then; after a lossy retype, the old value is shown as it was
rather than converted or dropped; and the conversation's earlier replies read exactly as they did.

*Why "as it was" and not "marked stale":* the engine already keeps the answer. Reading a version through
the current schema reports every value it cannot carry, with its reason (versioning plan, V-10), so
showing the field costs nothing and hiding it would be a choice to lose information the storage has.
Rewriting an earlier reply to match today's shape would make the conversation lie about what was said.

**TR-7 (S).** Diagnostics shall be prunable, with a stated retention default.
*AC:* a purge command removes traces older than the retention window and leaves data,
conversations and change history untouched.

**TR-8 (M).** Change history shall have its own, longer retention, and shall not be removed by
a diagnostics purge. The engine's version history for the assistant's collections is purged no earlier
than the start of the compensation window, so the window can never promise an undo whose versions are
gone.
*AC:* after a diagnostics purge, every change is still attributable to a request and a time, and D-17's
compensation still works for anything inside the compensation window. A configuration that would purge
version history inside the window is refused, naming both moments.

### 4.6 Interface (group UI)

**UI-1 (M).** One window with two surfaces — the conversation and the browser (group BR) —
reachable from a single persistent control. The conversation surface is the conversation on
the left and the diagram on the right, with the split adjustable and the diagram collapsible.
The application opens on the conversation.
*AC:* moving between the two surfaces loses neither the conversation's scroll position nor the
browser's selection.

**UI-2 (M).** Vocabulary is governed by **where** a string appears, not by whether a technical
word exists anywhere. Three tiers. In the **conversation and the browser's primary surfaces**,
no word may require knowing what a collection, column, index, schema or query is; there are no
exceptions there, because that is where the premise of section 1.3 lives. In the **diagram's
detail panel and a diagnostics area**, technical terms are expected, because inspection is
their whole purpose. **Between them**, a technical term may appear in a plain surface only with
a plain gloss beside it.
*AC:* a review of every string in the first tier finds no database vocabulary; the detail panel
is allowed to say "index seek" and does; BR-6's "what this keeps" panel, which already shows
structure in plain words, is the worked example of the third tier.

*Why the absolute rule was wrong:* TR-6 requires the detail panel to show the access path a
query took and the tokens a model call cost. A blanket ban leaves that panel either useless or
dishonest, and section 1.3's own argument — that someone who cannot see what the system did
will not trust it — argues for the exception rather than against it.

**UI-3 (M).** Attaching a file shall be possible by drag, by paste and by a picker, and the
application shall show what it understood about the file before anything is stored.
*AC:* dropping a spreadsheet shows the detected sheets, header row and column types, and what
is about to happen, before it happens.

**UI-4 (M).** A confirmation for a destructive change shall state the loss in counts and
examples, not in schema terms.
*AC:* the card reads as what will be lost and how much of it, and offers to proceed or not.

**UI-5 (S).** The assistant's reply shall stream.

**UI-6 (S).** Selecting an earlier request in the conversation shall show its diagram.

**UI-7 (M).** Everything shall be reachable **without a pointer**. A defined focus order across
the two surfaces and the switch between them; keyboard navigation of the table including paging;
focus restored to where it was after a confirmation closes; every control with a name a screen
reader can say; text that scales with the platform setting without clipping; and contrast that
meets the platform's own guidance, which the palette of `docs/design/` is checked against rather
than assumed to pass.
*AC:* each of S-1 to S-8 is completed keyboard-only; the accessibility tree is inspected on Mac
Catalyst and on Windows and every interactive element has a name and a role.

**UI-8 (M).** The diagram shall have a **parallel semantic representation**, because a drawn
surface is an empty rectangle to a screen reader. The same layout model renders as a focusable
list of steps carrying each step's name, status and outcome, in trace order, and selecting one
there opens the same detail panel a click opens.
*AC:* a screen reader announces the steps of a finished request in order with their outcomes;
keyboard selection moves between steps and the detail panel follows.

*Why this is cheap now and expensive later:* the layout is already computed as plain testable C#
(D-9), so the semantic list is a second renderer over a model that exists, and the detail panel
it opens is TR-6, which exists too. Retrofitted after the drawing is finished it is a second
implementation of the same traversal. Platform support also differs between the two desktop
targets, which is why R-8 records it as a risk rather than an assumption.

### 4.7 Browsing what is stored (group BR)

The second surface of D-13. Everything here is read-first: it exists so a person can see and
check their own things, not so they can administer a database.

**BR-1 (M).** A view listing everything stored: each with the name it is known by, a sentence
saying what it keeps, how many records it holds, and when it last changed. Ordered by most
recently changed.
*AC:* a storage with twelve things stored lists twelve entries, and no entry is described in a
word that requires knowing what a collection is.

**BR-1a (M).** The record count and the last-changed time shall be **maintained in the catalogue
document and updated in the same transaction as the write**, never computed by reading the
records.
*AC:* opening the overview issues no query whose cost grows with the number of records stored; a
reconcile command exists for drift and is idempotent, and a test asserts count and time are
correct after an import, a delete and a rolled-back transaction.

*Why this is a requirement and not an optimisation:* BR-1 shows how many records each thing holds
and when it last changed, ordered by most recently changed, on the screen that opens first. Read
literally and implemented naively that is a full scan of everything stored at startup, which
would put NF-3 out of reach on the very database it describes. The catalogue is already stored as
documents, so this is a field rather than a mechanism.

**BR-2 (M).** Opening one shows its records as a table. The display value leads; the remaining
fields follow in a stable, sensible order; the total count is shown.
*AC:* a thing holding 10 000 records opens in under two seconds and scrolls smoothly.

**BR-3 (M).** Paging shall be by **cursor**, never by offset: a continuation token built from
the ordered index key, loaded a page at a time, never by materialising the collection.
*AC:* memory does not grow with the number of records; the planner reports an index walk rather
than a full scan; and a page boundary that falls among many rows sharing a sort value neither
skips nor repeats one.

*Why this is cheap here specifically:* the B+Tree's range scan is a cursor already, and D-3's
composite key of value plus record identity makes the cursor unique even when the sort value is
not. A cursor on a non-unique sort key is what skips and repeats rows, and the engine's key
design removes that failure by construction.

*What it does not remove, stated rather than implied:* a record whose **sort value changes**
between two pages moves across the cursor, and can then appear twice or not at all. The composite
key solves ties on a stable sequence; nothing solves mutation without reading the whole collection
as of one moment, which the engine does not have: its versioning reads history one record at a
time, and a query as of a moment is that plan's F-3. So BR-3's criterion is about ties, and BR-3b
says what happens to the rest.

**BR-3a (M).** A cursor is valid only for the sort and filter it was issued for; changing
either starts a new sequence. Jumping to an arbitrary page is not supported.
*AC:* changing the sort resets to the first page rather than landing somewhere arbitrary.

**BR-3b (M).** Paging is **eventually consistent, and says so**: within one page sequence a
record edited concurrently may be seen twice or missed. Where the collection changes underneath an
open sequence the table shall show that it has, and offer to start again.
*AC:* editing a record so that it moves in the sort order, mid-sequence, produces a visible
"this has changed" affordance rather than a silently wrong list; the affordance is driven by the
last-changed value BR-1a already maintains, so it costs no extra query.

*Why this is the right trade here and not a general one:* writes serialise (AG-10) and the only
writer is the same person or their own import, so the window in which this happens is small and
always explicable to the user. Snapshot paging and a read timestamp both need a query as of a moment
(versioning plan, F-3), which is a temporal index; buying that for a table's paging would be building
the largest thing that plan left out, under another name.

**BR-4 (M).** Sorting and filtering shall be available from the table itself, expressed as the
same declarative query type the assistant uses (SC-7) and executed without a model.
*AC:* sorting and filtering cost zero tokens, and an applied filter is visible and removable.

**BR-5 (M).** A record opens in a detail view showing every field it has and the display value
as its title. A field with no value is shown as empty rather than omitted.
*AC:* a record written before a field existed shows that field, empty, rather than hiding it.

**BR-6 (M).** What a thing keeps shall be visible on demand: each field, what kind of value it
holds, whether it must be unique, whether it is required — written in plain words.
*AC:* every string in this panel passes the UI-2 review; the words "column", "type", "index",
"schema", "constraint" and "nullable" do not appear.

**BR-7 (S).** Where a relation exists between two things stored, the related records shall be
reachable in one step from the detail view, described by what the relation means rather than
by the columns that implement it.
*AC:* from a conference, the expenses that name it are one click away, under a heading a
person would write.

**BR-8 (M).** Changing anything from the browser shall go through exactly the rules the
conversation goes through: classified by what it can do under D-14 and handled by class under
AG-4, with the reversibility of AG-11c stated on the card, and every change recorded as a trace
(D-8) that appears in the diagram.
*AC:* deleting a record from the table produces a confirmation, a trace and a diagram entry
identical in kind to deleting it by saying so.

**BR-9 (S).** The two surfaces shall be connected in both directions: a row in a chat result
opens in the browser, and a record in the browser can be carried into the conversation as the
subject of the next thing said.
*AC:* clicking a result row opens that record; "tell me more about this" from the browser
begins a turn already about it.

**BR-10 (C).** What is on screen, with its sort and filter applied, can be saved as `.csv`.

### 4.8 Extensibility (group EX)

**EX-1 (M).** Adding a capability of the kind already anticipated — semantic types, display
values, validation rules, new column types — shall require no change to the storage format and
no migration of existing databases.
*AC:* a new field on a definition is a new field on a document; a database written before it
reads it as its default.

**EX-2 (M).** Adding an operation shall be a matter of declaring it: its model configuration,
its tool subset, its context assembly and its trace steps, without touching the orchestrator.
*AC:* a new operation is added in one file and appears in the diagram with no change to the
orchestrator or the diagram code.

**EX-3 (M).** Adding a document format shall be a matter of implementing one parser interface.
*AC:* a parser for a new extension is registered in one place and the ingestion pipeline uses
it without further change.

**EX-4 (S).** Adding a detail view for a step kind shall be a matter of registering it against
the kind, with JSON as the fallback.

### 4.9 Non-functional (group NF)

**NF-1 (M).** Token budgets per operation, measured and asserted. See section 6.

**NF-2 (M).** A retrieval of up to 100 records shall be shown within 2 seconds of the model
emitting the query, excluding model time, measured under NF-6.

**NF-3 (M).** Opening the application with a database holding 100 000 records and 1 000 traces
shall take under 2 seconds before the conversation list is usable, measured under NF-6, cold.

**NF-4 (M).** Egress is a **mode**, not a preference. `LocalOnly` is the default and *refuses*
a remote model endpoint rather than warning about one. `RemoteAllowed` is an explicit choice
carrying a permanent indicator in the interface.
*AC:* configuring a non-local endpoint in `LocalOnly` fails with a message naming the mode; in
`RemoteAllowed` the indicator is visible on every screen.

*Why the previous wording could not hold:* it said nothing the user stores leaves the machine
and in the same sentence permitted traffic to the model endpoint. Both are true only while the
endpoint is local, because the prompts **are** the user's data.

**NF-4a (M).** What may leave shall be declared per operation, since it genuinely differs: a
placement call sends a schema digest and a bounded sample of values, extraction sends raw text,
a query call sends no data at all. The declaration sits on the operation (AG-1), beside its
model configuration and context budget.
*AC:* each operation names its egress class, and a test asserts that no operation sends more
than it declared.

**NF-4b (M).** Egress and retention are separate concerns. Even in `LocalOnly`, step payloads
written to disk contain user content, so the redaction rule applies to **payloads**, not to
prompts — which D-8 already reduces to a hash.
*AC:* a trace of a request that stored personal data contains that data only where a payload
rule allows it, and the retention window for payloads is shorter than for the change history
(TR-8).

**NF-4c (M).** "Local" shall be **defined**, not assumed: `LocalOnly` permits loopback only, and
a LAN address is a remote endpoint. In `RemoteAllowed`, credentials are held by the platform's
own secret store and never in the database, never in a trace, and never in a settings document.
*AC:* configuring a model endpoint on another machine in `LocalOnly` fails naming the mode; a
credential is not recoverable from the database file by inspection.

**NF-4d (M).** The conflict between **deletion and durable undo** shall be resolved in the open.
Deleting a record leaves it restorable from version history for the compensation window, after which
its versions are purged and it is genuinely gone. **The confirmation card states the window** at the
moment of deletion. A purge exists that ends the window immediately, either for a given request (by
purging the history of the records it changed, `PurgeRecordHistory`) or for a given thing (`Erase`).
*AC:* after the window, no **reachable** copy of a deleted record remains: its versions are gone, no
query or history read can return it, and a test asserts both. The card says how long the record stays
recoverable.

**NF-4d1 (S).** Making the bytes unreachable is not making them absent, and the boundary of the
stronger claim shall be stated rather than glossed. The engine clears every byte it releases, and discards
the journal frame of a transaction that erased or purged anything as soon as it commits (versioning plan,
V-17). So an erase leaves no copy in the database file or its journal, without waiting for a compaction
or a close. A purge that ends the window early leaves no copy of a value that existed only in the
versions it removed (versioning plan, V-15). Erasing through the assistant additionally clears the step
input and output payloads of every request that changed the record; the change journal keeps the record's
identifier, an audit reference that carries no value (TR-2b). Outside the boundary, and said so on
the confirmation card: the user's own conversations, which they delete themselves (SC-10), plus process
memory, the operating system, the file system and backups.
*AC:* right after an erase through the assistant commits, a test searching the raw database file and its
journal for a distinctive value finds it only in the conversation entry the user typed. The card states
that conversations are kept. Right after a per-request purge commits, a value that existed only in the
purged versions is in neither file.

*Why not "opt in for sensitive data":* section 1.3 is that the user does not classify their own
data and should not have to. A window stated on the card is honest without asking them to
categorise anything, and it is the same card they are already reading.

**NF-4e (S).** At rest, the database shall rely on **OS-protected storage and restrictive file
permissions**, in the user's application data location, created private to the user.
*AC:* the file is not readable by another account on a shared machine. Encryption at rest is
**out of scope** and recorded as such: the engine has none, and implying otherwise would be a
promise this design cannot keep.

**NF-5 (S).** An ingestion of 10 000 rows shall complete within 30 seconds excluding model
time, in one transaction, measured under NF-6.

**NF-6 (M).** Every timing above shall be stated as a **measurement, not an adjective**. Each
carries: the generated fixture it is measured against, including record count, field count and
value shapes; whether it is **cold**, the first open after process start, or **warm**; the
**percentiles** reported, p50 and p95, over a stated number of runs; and the access path the
planner is required to take, so a number met by a full scan on a small fixture fails anyway.
The machine is **reported with the result** rather than fixed in the requirement.
*AC:* the performance report names fixture, mode, run count, both percentiles, the planner path
and the machine; a run whose planner path is a scan where a seek was required fails even when
the time is met.

*Why the machine is reported and not specified:* a threshold pinned to named hardware ages out of
a dissertation faster than the dissertation is written, and it makes the number unreproducible on
anything else. A fixture, a method and a reported machine reproduce anywhere.

---

## 5. The scenarios that define "done"

These are the acceptance scenarios for the application as a whole. They are written from the
user's side and they are what the token harness measures.

**S-1. Storing prose.** The user writes: *"I want to save these — the conference in Lviv on
14 March, 12 000 hryvnia, and the one in Kyiv in May, 8 500."* The system extracts two
records, finds no collection that fits, proposes one, creates it, stores both, and says what
it did in a sentence. The diagram shows intent, extraction, mapping, creation and write.

**S-2. Storing a spreadsheet into something that exists.** The user drops
`conferences-2025.xlsx`. The system parses it, recognises it as the same shape as the
collection made in S-1 plus one new column, adds the column without asking, stores the rows in
one transaction, and reports how many were stored and how many were not, with reasons.

**S-3. Retrieval.** *"How much did I spend on conferences last year?"* The system writes a
query, runs it, and shows the answer and the records behind it. No rows enter a model context.

**S-4. Follow-up.** *"Which was the most expensive?"* Answered from a digest of the previous
result.

**S-5. Correction.** *"The Lviv one was actually 13 000."* The system finds the record, updates
it, and the trace shows 12 000 beside 13 000.

**S-6. Destructive change.** *"Drop the notes I was keeping on these."* The system shows that
14 records hold a value for it, with three examples, and waits.

**S-7. Reopening.** The application is closed and opened again. Every conversation, every
record, and every diagram is there, and S-4 still works against the result from S-3.

**S-8. Looking for oneself.** Without saying anything to the assistant, the user opens the
browser, sees the things they have stored, opens the conferences, sorts by cost, opens the
Lviv one, and sees that it now reads 13 000 — the correction from S-5. They then ask what it
keeps, and get a list of fields in words they would use. No model is called at any point, and
the token harness records zero tokens for the whole sequence.

---

### The ones that are not happy paths

The seven above are what working looks like. These are what this product actually has to
survive, and each asserts **what the user is told**, not only what the system does — because
the failure that matters here is a confident wrong answer, not a crash.

**N-1. Two candidates, neither clearly right.** A file could plausibly be conferences or trips.
The system asks, showing both with what it matched on, rather than picking (AG-3b).

**N-2. A valid but wrong placement.** The file goes somewhere plausible and wrong, which no
test can detect directly. What is asserted is that the decision is visible in the reply and the
diagram, and correctable in one turn.

**N-3. The same file twice.** 22 in, 22 stored, then 22 in again and 0 stored, reported as
skipped with the key that matched, per row and not as a count (IN-5 to IN-8).

**N-4. A malformed file.** 100 rows, 3 unparseable: 97 stored, 3 reported with line numbers and
reasons, in one batch (IN-4).

**N-5. An empty one.** Headers and no rows; a message with no content. Neither creates anything
and both say so.

**N-6. The model fails.** Malformed output past the retry bound, and a timeout. Both end with a
message naming what could not be done, and neither leaves a partial write.

**N-7. Cancellation.** The user cancels mid-request. Nothing is half-applied and the state
reaches `Cancelled` (D-15).

**N-8. A refused confirmation.** The user says no to a destructive change. Nothing happens, the
request completes, and the refusal is in the trace.

**N-9. A crash while waiting.** The application is killed with a confirmation on screen.
Reopening restores the question, and answering it executes what was shown (AG-8).

**N-10. A schema change underneath.** A placement computed against one schema, the schema
changed before the write. The request re-plans (AG-9).

**N-11. Undo.** An import is reversed and the storage is as it was. The same after an unrelated
change still works. After a change to *those* records — including one changed and changed back — it
refuses before writing anything, names every record that blocks it, and offers the rest as a separate
action, which the user then either takes on a card of its own or does not. An undo that would put back a
unique value another record has taken since refuses whole and names that record (AG-11, AG-11a, AG-11b,
AG-11c).

**N-12. A substituted proposal.** Between the confirmation and the write, the proposal is
replaced. The write is refused on the content hash rather than applying something the user never
saw (AG-3d). This is the one negative scenario with no user-facing story: it exists so that AG-8's
promise has a test that can fail.

---

## 6. The token budget

This is the central non-functional design, so it is specified rather than left as an
aspiration.

### 6.1 Where the tokens go, and what is done about it

| Cost | Untreated | Treatment |
|---|---|---|
| Tool definitions | Grows with every capability; charged on every call | **D-5**: per-operation subsets. A retrieval call carries one tool |
| Schema | Grows with the user's storage | A compact digest — name, purpose, `column:type` — of only the collections a C# pre-filter finds plausible, with a count of the rest and a tool to ask for more |
| The data itself | Grows with the file | **IN-3**: profile plus a bounded sample for tables; budgeted chunks for prose |
| Results | Grows with the answer | **D-6**: results are never returned; a digest on follow-up only |
| History | Grows with the conversation | **AG-6**: sliding window plus rolling summary |
| Instructions | Fixed but large if one prompt must cover every job | **D-4**: one small prompt per specialised call |
| Re-reading | Doubles on every retry | Structured output with a validating repair loop that sends the error, not the whole request again |

A further lever: keep the system prompt and tool block byte-identical between calls of the
same operation, so Ollama's prefix cache is reused. A prompt assembled in a different order
each time defeats it silently, which is why AG-6a makes it an asserted property rather than a
habit: a reordering of two blocks is invisible in review and costs the whole cache.

**What the prefix cache does not buy, so that it is not relied on for the wrong thing.** It buys
**latency**, not headroom. The cached prefix occupies exactly as much of the window as an
uncached one, and on a local model there are no per-token charges for it to reduce. The three
things that address context pressure itself are the enforced context limit of the Phase 0.3 gate,
the bounded projection of AG-6 and AG-1a, and operations that carry no tools at all under D-5,
where the first tool alone costs more than the digest it would have fetched.

### 6.2 Budgets

Per model call, prompt side, measured by the harness.

**Measured, step 0.4 (2026-09-15).** The digest is the figure that needed a measurement, and it
has one: on `qwen3.5:4b` the mapping call finds the right thing 14 times in 20 with a digest of
1,000 prompt tokens, 7 in 20 at 4,000, 3 in 20 at 8,000. So the digest in the prefix is bounded at
1,000 tokens and the model chooses from a shortlist of at most five (AG-3c), which leaves the
mapping call's 3,000 with room to spare; the other rows stood as they were, since nothing in the
measurement asked them to move. The ceiling they sit under is the 16k window step 0.3 made a
per-call option. `scratch/ContextSpike/FINDINGS.md`.

| Operation | Budget | Note |
|---|---|---|
| Intent classification | 600 | No tools, no schema, last turn plus the attachment kind |
| Extraction (prose to records) | 4 000 | One text chunk plus an output shape |
| Mapping (records to storage) | 3 000 | Schema digest of candidates plus a record shape, never the records |
| Structural proposal | 2 000 | One collection's digest plus the proposed change |
| Query writing | 2 500 | Schema digest of candidates plus one tool |
| Result digest answering | 1 500 | The digest, not the rows |
| Reply phrasing | 1 200 | Outcome facts, not payloads |

**Whole-scenario budgets**, summed over every model call in the request:

| Scenario | Budget |
|---|---|
| S-1 store prose, new collection | 9 000 |
| S-2 store 1 000-row spreadsheet into an existing collection | 7 000 |
| S-3 retrieval | 4 500 |
| S-4 follow-up | 2 500 |

**S-2's budget must not move when the row count does.** That is the property the harness
exists to protect, and the one most likely to be lost by accident.

### What has been measured against these

One operation has real numbers, from `scratch/AgentFrameworkSpike`. The mapping call, twenty
attempts, `qwen3.5:4b`:

| mapping call | prompt tokens, whole operation | valid output |
|---|---|---|
| budget above | 3,000 | |
| with a read tool for the digest | median **46,943** | 6 of 20 |
| with the digest in the system message | about **215** | 14 of 20 |

The budget is not the thing that needs adjusting. One arrangement misses it by fifteen times
and the other meets it with an order of magnitude to spare, on the same model and the same
schema — so the budgets stand and the arrangement is what section 7's R-1 condition is about.

### 6.2a What is measured, which is more than prompt tokens

A prompt-token budget alone reports 210 for a call that generated 3,886 completion tokens and
took most of the wall clock. Every scenario carries all of these, and every one is asserted:

| | why it is here |
|---|---|
| prompt tokens | the budgets of 6.2 |
| output tokens | the spike's failures were unbounded generation, not long prompts |
| total tokens | what the run actually cost |
| model calls | AG-1b; splitting multiplies failure |
| **round trips** | a single logical call became 25 of them, and the aggregate hid it completely |
| retries | a repair loop that fires often is a budget leak |
| **end-to-end success rate** | AG-1c; the gate, measured over repeated runs rather than predicted |
| failures by mode | AG-1e; a repaired malformed output and an empty reply are different events |
| latency, end to end | AG-1b; on a local 4B this is what the user feels |
| **peak context** | a hard assert, not a metric: this is what triggers the context shift that empties replies |

Two of these carry the weight. **Round trips** must be counted separately from model calls,
because `AgentResponse` sums the turns and made a 22-call loop invisible. **Peak context** is
the leading indicator of the failure mode that actually occurred, so it fails a test rather
than appearing in a report.

**A methodology constraint, not a preference.** Ollama's `prompt_tokens` is unreliable after a
generation that filled the context: identical request bodies reported 219, 2848, 1965, 215 and
3300. The harness therefore measures with a single-token probe call, which is how the overhead
tables of `scratch/AgentFrameworkSpike/FINDINGS.md` were produced. Reading the figure off a
working call records fiction, and TR-3's accounting is subject to the same rule.

### 6.3 How it is enforced

`TokkDb.Assistant.TokenBudget` runs the scenarios against a recorded or fake model, reads every
figure in 6.2a from the trace (TR-3 records them anyway), and writes a table to
`docs/assistant-token-budget.md`. A scenario over any of its limits fails a test.

It also measures each scenario's **end-to-end success rate** over repeated runs, which is the
gate (AG-1c), and reports the product of the per-call rates beside it as a diagnostic (AG-1d).
The measured figure, not the token count and not the predicted one, is what decides whether an
operation may be split further, and the distance between the two says whether the failures share
a cause. Each figure states the number of runs behind it, and the assertion is against the lower
bound of the interval: a floor claimed on a handful of runs is a claim about the sample.

---

## 7. Risks, and the spikes that retire them

Each of these is cheap to test and expensive to discover late. Do them before the phase that
depends on them.

**R-1. Microsoft Agent Framework against Ollama. — Retired, with a condition.**
`scratch/AgentFrameworkSpike/FINDINGS.md`. Tool calling, automatic function invocation and
grammar-constrained structured output all work over the OpenAI-compatible endpoint with no
adapter, on `qwen3.5:4b`. The condition is that **the function-calling loop must be bounded
explicitly**: left at its defaults it was a 25-times cost multiplier and a 65% failure rate,
and the only thing ending it was the 4,096 window overflowing.

**R-2. Structured output from a small model. — Retired, and it was the wrong worry.** Over 20
attempts the model produced **zero** malformed JSON. The single schema violation was on
`minItems`, a keyword the framework demotes to a description before Ollama ever sees it, so the
grammar was never given it. The failures were *empty replies*, from a different cause
(the loop, and an output cap that is not in effect). Two things carry forward instead of a
repair loop: **every bound in the schema must be re-checked in C#**, because the grammar
enforces `type`, `enum`, `required` and `additionalProperties` and nothing else; and the
schema itself is free in prompt tokens, since Ollama compiles `response_format` into a sampling
grammar rather than putting it in the prompt.

**R-2b. The output cap is not in effect. — Closed by step 0.3 (2026-09-14):** over the native
transport `MaxOutputTokens` becomes `num_predict` and a cap of 5 produces exactly 5 tokens with
`done_reason: length`. The paragraph below records what was wrong. `Microsoft.Extensions.AI` sends
`max_completion_tokens`, which Ollama ignores; it honours `max_tokens` (and `num_predict`
natively). Measured: the same request capped at 5 produced 5 tokens with one field and 1,866
with the other. Until this is fixed nothing limits generation, unbounded reasoning is what
empties the replies, and **no budget the harness of D-12 asserts is actually enforced**.

**R-3. 64k context on a 4B model. — Retired by step 0.4 (2026-09-15).** The window is not the
usable length, and the measurement says where the usable length ends for the mapping call:
14/20 correct at a 1k-token digest, 7/20 at 4k, 3/20 at 8k, 4/20 at 16k, every answer well-formed
throughout. The failure has one shape - the model answers `new` with the very name in the digest,
34 of 52 wrong answers - so the pre-filter is the mechanism (a shortlist of five, a digest of at
most 1,000 tokens) and a `new` naming an offered thing is read as the choice. §6.2 is marked
measured. `scratch/ContextSpike/FINDINGS.md`.

**R-4. `GraphicsView` on two platforms. — Retired on Mac Catalyst; open on Windows, and the
result was read too widely.** `scratch/DiagramSpike/FINDINGS.md`. Tap coordinates and canvas
coordinates are the same coordinates **on a density-2 display**, which is one display at one
scale. A window moved to a monitor with a different scale factor is the case the spike did not
cover, and it is the common one on a desktop. TR-5a therefore requires the two to share a
transform rather than to be assumed equal, which is also testable without a second monitor.
Windows has not been run; there is no Windows machine here.

**R-5. `.docx` and `.xlsx` without Office.** `DocumentFormat.OpenXml` handles both, at the cost
of doing the reading by hand; `ClosedXML` is friendlier for spreadsheets and adds a dependency.
*Spike: parse a real, ugly file of each with the chosen library.*

**R-6. The storage contract is a phase, not a step.** D-1 means rebuilding what took several
phases the first time. It is smaller now because the answers are known, but it is not small.

**R-7. The native-transport question. — Retired by step 0.3 (2026-09-14): native.** See D-3's
decision; the five measurements are in `scratch/TransportGate/FINDINGS.md`. `num_ctx` and
`num_predict` are both Ollama options that its OpenAI-compatibility layer drops. An
`IChatClient` speaking `/api/chat` fixes R-2b and D-3's context in one move, and
`Microsoft.Agents.AI` keeps working over it because it is written against `IChatClient`. What
is unproven is whether tool calling and schema-constrained output are as good over the native
path. *Spike: the same 20 attempts through a native client, compared against the numbers
already in hand.* **This is a gate, not a spike to schedule when convenient:** Phase 5 does not
begin until it passes or an alternative transport is chosen, and Phase 8's budgets assert nothing
real until R-2b is closed.

**R-8. Accessibility across two desktop platforms. — New.** MAUI's accessibility support is not
identical on Mac Catalyst and Windows, and the diagram is a drawn surface with no accessibility
tree of its own (UI-8). *Check early, on the spike of 0.2, what a screen reader makes of a
`GraphicsView`, because the answer changes how the semantic list is built rather than whether it
is.*

**R-9. Undo depends on the engine's versioning plan. — Retired on 2026-09-14.** D-17 undoes record
changes from version history, which `docs/versioning-requirements-and-plan.md` has now built: its
Phases 1 to 9 are on this branch, and its step 9.3 wrote the compensation as a test-local helper over
`IStorage` (`TokkDb.Assistant.Tests/Compensation.cs`, with `CompensationTests` and `UndoGuaranteeTests`
against both backends) for step 4.7 to lift into `TokkDb.Assistant.Agents` unchanged. *What is left of
it:* the helper is exercised only by its own tests until step 4.7 wires it to a request; and the built
`DiffVersions` drops what the engine reports as unmapped (SC-12, TR-6a), which step 7.3 closes before the
detail panel is built on it.


---

## 8. Development plan

Ten phases. Each states its goal and the condition for calling it done. The prompts in
section 9 correspond to the steps within them.

**Milestone A — the thin path.** One sentence stored and read back, through the real transport
and a real model: text in, deterministic placement into one thing, an atomic write with its
durable change record, a retrieval, and a minimal chat display. It is a **milestone that cuts
across phases 1 to 5 at their earliest usable point**, taken as soon as those five have the
little of each that it needs, and it is not a reordering of them.

*Why a milestone and not a resequencing.* The risk this answers is integration arriving late, and
that risk is bought off by pulling one path forward rather than by rebuilding the plan around a
narrow slice: the storage contract was chosen as a full rebuild for reasons D-1 records, and
widening a contract later would keep re-running the same suite while the documents underneath it
change shape. The thin path costs a fraction of a phase and tells us the same thing. It also
lands together with the transport gate above, since both exist to find out early whether the
central assumption holds.

**Phase 0 — Spikes and the transport gate.** R-1, R-4 and R-7. Throwaway code in a scratch
project, except that the last of them is a gate rather than an experiment.
*Exit:* a 4B local model completes one tool call with validated structured output; one drawn
block responds to a click on both platforms; and the transport question is **decided** — a
native client that enforces a context limit and an output limit while keeping tool calling and
schema-constrained output, or a recorded decision to stay on the OpenAI-compatible path with the
environment variable and the parameter name Ollama actually honours; and R-3 is measured, so that
§6.2's budgets stop being provisional before anything is built against them.

**Phase 1 — The storage contract.** `TokkDb.Assistant.Storage` and `.Storage.Engine`: the
contract, the definitions, validation, the implementation over `TokkDbConnection`, the batch
API, and contract tests run against both the engine and an in-memory fake.
*Exit:* the contract test suite passes against both implementations; no `TokkDb.LLM.*`
reference exists.

**Phase 2 — Ingestion.** `TokkDb.Assistant.Ingestion`: the parsers, type inference, profiling,
the model-facing renderings. No model, no storage, entirely deterministic.
*Exit:* the awkward-file tests of IN-1 and IN-2 pass; the rendering bound of IN-3 is asserted.

**Phase 3 — The trace.** `TokkDb.Assistant.Trace`, the reserved collections, the recorder, and
persistence through the engine's `SystemDocumentStore`, under the two durability rules of TR-4
and TR-4a rather than one. Record changes' version references (TR-2b) arrived with the versioning
plan's step 9.1.
*Exit:* a hand-built trace survives a reopen whole; a data change and its trace step commit
together or not at all.

**Phase 4 — Operations and the orchestrator.** `TokkDb.Assistant.Agents`: the operation
declaration, tool scoping, context assembly, the model configuration, the Microsoft Agent
Framework orchestrator, the repair loop, and a deterministic fake model. Compensation (step 4.7)
adopts what the versioning plan's step 9.3 built (R-9, retired).
*Exit:* S-1 to S-8 and N-1 to N-12 run end to end against the fake model, producing the right
storage effects and the right trace, with no model running.

**Phase 5 — The shell.** `TokkDb.Assistant.Application` (the plan's `TokkDb.Assistant.App`): window, chat, attachments, streaming, the
conversation list, settings, and the composition root.
*Entry:* the transport gate of Phase 0.3 has passed, or an alternative transport has been chosen
and recorded. No real-model integration is written against a transport that enforces neither
limit.
*Exit:* S-1 and S-3 work against a real local model, with the trace visible as raw JSON.

**Phase 6 — Browsing what is stored.** The second surface of D-13: the overview, the
virtualised table, sorting and filtering, the record detail, the plain-words structure panel,
and the bridge to the conversation. No model is involved in any of it.
*Exit:* BR-1 to BR-8 pass; a thing holding 10 000 records opens in under two seconds
and the planner reports an index walk rather than a scan; S-8 costs zero tokens.

**Phase 7 — The diagram.** `TokkDb.Assistant.Diagram` and its host: layout, drawing, live
growth, hit testing, the detail panel, and the three specialised views.
*Exit:* TR-5, TR-5a, TR-6 and TR-6a pass; the diagram is live during a request, correct after a
reopen, and correct at three scale factors.

**Phase 8 — The budget.** The harness, the scenario measurements, the published table, and the
tuning the measurements call for.
*Exit:* every limit in sections 6.2 and 6.2a is met and asserted; S-2's cost is flat in the row
count; each scenario's **measured** end-to-end success rate is above its stated floor, with the
run count stated and the predicted figure reported beside it.

**Phase 9 — Rounding off.** Retention and purge, the correction path (QR-4), the destructive
confirmation card, error messages, the vocabulary review of UI-2 and BR-6, the accessibility pass
of UI-7 and UI-8, and the performance report of NF-6.
*Exit:* S-1 to S-8 and N-1 to N-12 pass against a real model on both platforms, keyboard-only as
well as with a pointer.

---

## 9. Development steps, as prompts for Claude Code

One step per message. Each names what it implements and states when it is done. They are
written to be pasted as they are.

### Phase 0

**0.1 Agent framework spike**
> Read docs/assistant-requirements-and-plan.md risk R-1. In a scratch project, make one
> Microsoft Agent Framework call against local Ollama through its OpenAI-compatible endpoint,
> with one tool the model must call and a structured output that is validated against a schema.
> Report the model used, whether tool calling worked, how often the structured output was
> malformed over 20 attempts, and the prompt tokens of the fixed overhead. Done when: the
> numbers are in the message and the spike code is in the scratch directory, not the solution.

**0.2 Drawn-diagram spike**
> Read docs/assistant-requirements-and-plan.md risk R-4 and decision D-9. In a scratch MAUI
> project, draw two connected blocks in a GraphicsView and make each respond to a click by
> reporting which was hit. Build and run it on Mac Catalyst. Report whether text measurement
> and hit testing behave, and what differs for Windows. While it is running, turn the platform
> screen reader on and report what it makes of the drawn surface, which risk R-8 needs and which
> decides how UI-8's semantic list is built. Done when: clicking either block is distinguishable,
> the Windows differences are written down, and the screen-reader behaviour is recorded.

**0.3 The transport gate**
> Read risks R-2b and R-7 and decision D-3. In the scratch project, run the same twenty attempts
> as 0.1 through an IChatClient that speaks Ollama natively, keeping Microsoft.Agents.AI above it.
> Report five things against the numbers already in hand: whether the context limit is enforced,
> whether the output limit is enforced, whether tool calling still works with the loop bounded
> explicitly, whether schema-constrained output is as good, and whether the approval-gated
> function path still behaves. Done when: all five are measured over twenty runs and the
> transport is **decided** — native, or the OpenAI-compatible path with OLLAMA_CONTEXT_LENGTH and
> the parameter name Ollama honours, recorded with the reason. Phase 5 does not start until this
> step has an answer.

*Done 2026-09-14: native.* `scratch/TransportGate` is the spike and its `FINDINGS.md` the five
measurements and the reason; D-3, R-2b and R-7 carry the result.

**0.4 What the context is actually worth**
> Read risk R-3 and requirement AG-1a, and run this after 0.3 has decided the transport. Measure
> mapping accuracy against the size of the schema digest at 4k, 8k and 16k, twenty attempts each,
> and set the pre-filter's aggressiveness and §6.2's budgets from the answer rather than from
> taste. Done when: the three measurements exist, the budget table is marked measured rather than
> provisional, and the digest size at which accuracy starts falling is written down.

*Done 2026-09-15.* `scratch/ContextSpike` is the spike, at 1k as well as the three sizes; accuracy
starts falling before 4k and is gone by 8k; R-3 and §6.2 carry the result, and
`OrchestratorOptions.DigestBudget` carries the number.

### Phase 1

**1.1 The contract**
> Read docs/assistant-requirements-and-plan.md requirements SC-1, SC-2, SC-3, SC-4 and EX-1 and
> decision D-1. Add TokkDb.Assistant.Storage: IStorage, CollectionDefinition, ColumnDefinition,
> StorageRecord, ColumnType, and the typed errors. Settle the four open questions of SC-4 in the
> contract's own documentation rather than leaving them to an implementation. Shape the definition
> so that a capability added later is a new field on a document that an older database reads as
> its default (EX-1). No reference to any TokkDb.LLM.* project. Done when: the project builds, the
> definitions carry nothing physical, a reflection test asserts it, and a definition written
> before a field existed still loads.

**1.2 The contract tests, and a fake**
> Read requirement SC-4. Add TokkDb.Assistant.Tests with an in-memory IStorage and an abstract
> contract test suite covering identity, validation, uniqueness, name comparison and GetAll
> ordering. Done when: the suite passes against the in-memory implementation and is written so
> a second implementation only has to supply a factory.

**1.3 The engine-backed implementation**
> Read requirements SC-1 to SC-3 and decision D-2. Add TokkDb.Assistant.Storage.Engine
> implementing IStorage over TokkDbConnection: definitions in the catalogue, records as
> documents, every column type stored as itself. Read TokkDb.LLM.Storage.Engine first for what
> it learned, but reference nothing from it. Done when: the contract suite from 1.2 passes
> against this implementation as well.

**1.4 Batch, structural change, migration and relations**
> Read requirements SC-5, SC-6, SC-6a, SC-6b, SC-6c, SC-6d, SC-8 and SC-8a. Add the unit-of-work API and the
> structural operations — add, rename, retype and remove a column, remove a collection, add and
> remove a relation — over the engine's lazy migration, with the converge command exposed.
> Widening conversions are lossless and silent; every other direction counts the stored values
> that will not convert before the change is proposed. A value that cannot convert is kept and
> flagged, never dropped and never left unreadable. Every relation carries an integrity action
> with restrict as the default, and cascade is an explicit choice recording each deletion
> separately. Done when: an import of 500 records is one commit, a failed import leaves nothing, a
> retyped column serves records from both sides of the change, a record with an unconvertible
> value is still readable and marked, a range query over a column with pending values reports how
> many records it could not consider rather than quietly omitting them, converge is idempotent and
> reports what it could not convert, and deleting a record four others point at is refused by
> default naming those four.

**1.5 The query model**
> Read requirements SC-7, SC-7a, SC-9, SC-9a, BR-3, BR-3a and BR-3b and decision D-16. Add the
> query model in
> two faces: the rich internal one (projection, filter, sort, limit, cursor paging, aggregation,
> relation traversal) and the narrower model-facing subset that C# expands into it. Cursor
> paging is built on the composite index key, so a page boundary among equal sort values neither
> skips nor repeats; a record that moves in the sort order mid-sequence may still be seen twice or
> missed, which is stated rather than prevented. Add QueryExecutionInfo as a storage-owned type
> speaking in index seek, range walk, scan, records examined and records returned, mapped from the
> engine's QueryReport by the adapter and reported honestly by the fake, which performs a scan and
> says so, and carrying the count of records excluded as not of the column's type. Add the ordered
> set check of SC-9: a set of values in, encoded and sorted into key order, the index walked once,
> with per-value seeks kept as the small-set branch. Record whether the B+Tree can walk leaves
> without descending through the internal nodes again for every key, because that is what decides
> whether the bound is one page each or the pages of the range. Done when: an invalid query is refused with
> errors naming what did not fit, checking 10 000 values against 100 000 records touches each index
> page at most once and a test asserts the page-read count, paging a
> collection with 10 000 equal sort values is exact, the seek assertions live in the engine tests
> rather than the shared suite, and adding a feature to the internal model requires no change to
> what a model emits.

**1.6 Conversations and the display value**
> Read requirements SC-10 and SC-11. Add the conversation as a stored thing — created on the first
> turn, appended per turn, listed by most recent activity, renameable, deletable, with each turn
> recording who spoke, the text, its attachments by reference and the request it started. Add the
> display value with its rule and its fallbacks: the display rule where set, else the first
> required text field, else the first text field, else a shortened identity. Done when: a
> conversation and its turns survive a close and reopen, deleting a conversation leaves the data
> its requests stored and says so, and a collection with no text field at all still shows a
> sensible title.

### Phase 2

**2.1 Tabular parsing**
> Read requirement IN-1. Add TokkDb.Assistant.Ingestion with CSV and XLSX parsing: delimiter
> and encoding detection, header detection, per-column type inference and profiling,
> multi-sheet. Choose the spreadsheet library and say why. Done when: a file with a preamble
> before the header, mixed decimal separators and a 98%-integer column parses correctly and the
> exceptions are listed.

**2.1a Inference that admits what it does not know**
> Read requirements IN-1a and IN-1b. Make inference return a type, a confidence, evidence as
> counts with quoted examples, and any ambiguity. Where candidates are close the wider type
> wins. Done when: a column of 007, 013, 142 is text with the leading zeros as the evidence, a
> date column whose days are all twelve or under reports the day-month ambiguity rather than
> choosing, and a test pins that the narrowing direction is never taken automatically.

**2.2 Prose parsing**
> Read requirements IN-2 and EX-3. Add DOCX and TXT to plain text, keeping headings, lists and
> table content distinguishable, behind the one parser interface a new format implements. Done
> when: a docx with all three converts to text in which all three are still apparent, and a parser
> for a new extension is registered in one place with no other change.

**2.3 What the model sees**
> Read requirement IN-3 and section 6.1. Add the model-facing renderings: a compact profile
> plus a bounded row sample for tables, budgeted chunks for prose. Done when: the rendering of
> a 50 000-row file is the same size as that of a 500-row file, asserted by a test.

**2.4 Identity, separately from shape**
> Read requirements IN-4, IN-5, IN-6, IN-6a, IN-6b, IN-7, IN-8, IN-8a, IN-9 and IN-9a. Keep "is this
> the same kind of thing" and "have I seen this row before" apart. Propose a natural key with the
> model and confirm it deterministically in C# by checking uniqueness and non-nullness **both
> across the incoming data and against the records already stored**, creating or verifying the
> uniqueness rule that makes it a key. Fall back to a stored, indexed fingerprint over normalised
> values, carrying the normalisation version it was computed under, and checked for the whole
> import in one ordered pass under SC-9 rather than one lookup per row. The default merge policy is
> skip existing, on the key where there is one and on the fingerprint where there is not; add all
> is an explicit choice. Give every row a disposition of an outcome plus a reason and persist it
> with the change record. Classify every row before the write transaction opens, so a rejected row
> never fails the import and a fault always rolls it back. Done when: importing the same file
> twice stores 22 records and not 44 **whether or not the file has a natural key**, every row's
> outcome and reason is recoverable from storage, a candidate key that collides with stored
> records is rejected without asking the user, 97 of 100 rows commit while 3 are reported with
> their line numbers and reasons (IN-4), a
> cancellation leaves nothing, and undoing the second import is a no-op because every row was
> skipped.

### Phase 3

**3.0 The Trace project**
> Read section 3.1. Add TokkDb.Assistant.Trace holding the trace model and the recorder
> interface and nothing else: no behaviour, no dependencies. Agents, Diagram, Storage.Engine,
> TokenBudget and App all reference it; Agents does not reference Diagram and Diagram does not
> reference Agents. Done when: the solution builds, and a dependency test asserts the graph is
> acyclic and that nothing but App references Diagram.

**3.1 Reserved collections for traces**
> Read decision D-2 and requirement TR-4. Add the trace collections to
> TokkDb.Pages/CollectionCatalog/SystemCollections.cs and confirm Initialize creates them in a
> database written before they existed. This is the only engine change this plan calls for.
> Done when: an existing database gains the collections on open and the engine suite still
> passes.

**3.2 The trace model and recorder**
> Read requirements TR-1, TR-2, TR-2a, TR-2b, TR-3, TR-7 and TR-8 and decision D-8. Settle here
> how many reserved collections the trace actually needs, given three retentions between
> diagnostics, the change journal and conversations, and record the number against D-2, which no
> longer guesses it. Add two models with
> two retentions: RequestTrace, ExecutionStep and ModelCall as prunable diagnostics, DataChange
> as the durable audit record, joined by one id. Payloads are shaped by operation first and
> bounded by size second: an insert records the identity, an update the changed fields, a delete
> the whole record. Model-call steps record model, token counts, duration and a prompt hash,
> never prompt text. Done when: a hand-built trace of eight steps survives a close and reopen
> whole, purging diagnostics leaves every DataChange intact, and importing 10 000 records
> produces a change record that does not grow with the width of the rows.

*Revised by draft 8, after this step was done (`8e18bfb`).* Record changes no longer carry field
values: they carry the two version identifiers of TR-2b, and structural changes keep the payload this
step built. That change is made by step 9.1 of `docs/versioning-requirements-and-plan.md`, not by
running this step again.

**3.3 Two durability rules, not one**
> Read requirements TR-4, TR-4a, TR-4b, TR-4c and TR-4d and decision D-11. Commit the DataChange
> record in the same transaction as the mutation it describes. Persist lifecycle and diagnostic
> steps independently as they happen, linked by the request id, since the diagram grows during
> the request and a confirmation survives a kill before any write exists. Give every step a
> status, reconcile steps left running at startup as interrupted, and make the durability point
> explicit: lifecycle transitions durable at every transition, diagnostic steps within a bounded
> delay. Done when: an injected failure between the data write and the change write leaves
> neither, a request killed while waiting has its state and steps on disk, and a step interrupted
> by a kill reads as interrupted rather than as done.

**3.4 Request state**
> Read decision D-15 and requirements AG-8, AG-8a and AG-8b. Add the persisted request state —
> Running, WaitingForUser, Resuming, Completed, Cancelled, Failed — with WaitingForUser holding
> the validated, resolved intent rather than the conversation. Open the database under a
> single-writer lock, make every transition a compare-and-swap on the request's transition
> counter, and use the proposal's content hash as the idempotency key of a resolved action. Define
> startup recovery: Running cannot be resumed and becomes Failed with a reason of interrupted
> unless its DataChange shows the mutation committed, WaitingForUser is restored as it was, and
> Resuming is claimed atomically before work is redone. Done when: the process is killed while a
> confirmation is pending and answering it on reopening executes exactly what was shown with no
> further model call, a double-submitted confirmation produces one write, opening the same
> database twice reports it is in use, and each of the three recovery paths is a test.

### Phase 4

**4.1 Operations**
> Read requirements AG-1, AG-1a, AG-1b, AG-1f, AG-7 and EX-2 and decisions D-4 and D-5. Add
> TokkDb.Assistant.Agents
> with the operation declaration: name, model configuration, tool subset, context budget. Add the
> tool catalogue and the scoping that gives an operation only its own tools. Make calling a model
> outside an operation impossible rather than forbidden: the chat client type internal to this
> assembly and visible only to the tests, one public runner that takes an operation and does the
> context check and the trace recording itself, and that runner the only place the client is
> resolved from the container. Done when: a test enumerates the operations and asserts each
> declares all three, the assembler refuses a context over an operation's budget rather than
> trimming it quietly, a new operation is added in one file with no change to the orchestrator,
> an architecture test asserts no type outside this assembly references the client type, and a
> second asserts the container hands it to nothing else.

**4.2 A model that is not a model**
> Read decision D-12. Add a deterministic fake model that answers from a script, can be made to
> return malformed output, and records what it was asked. Done when: an operation can be driven
> end to end with no Ollama running.

**4.3 Context assembly**
> Read section 6.1 and requirements AG-6 and AG-6a. Add the schema digest, the C# pre-filter that
> chooses which collections are plausible, the sliding window and the rolling summary. Assemble the
> prefix in one order — instructions, schema, tools — and assert it is byte-identical between calls
> of one operation rather than trusting that it is. Remember what that buys: latency through
> Ollama's prefix cache, and no headroom at all, since the cached prefix fills the same window.
> Done when: the digest of a 200-collection storage is bounded, a 200-turn conversation stays
> within budget, and a test hashing two prefixes of one operation fails when their order differs.

**4.4 The orchestrator and the placement proposal**
> Read requirements AG-2, AG-3, AG-3a, AG-3b, AG-3c, AG-3d and AG-5. Add the orchestrator, and make the
> placement one object: target, field mappings with their kind and types, unmapped incoming
> fields, unfilled existing fields, structural actions, confidence with evidence, rationale.
> Confidence is computed in C# from observable evidence and never read from the model. Apply
> silently only when the best candidate clears a floor and its margin over the runner-up clears
> a gap; a new thing needs a higher bar than an existing one. The model ranks a C#-produced
> shortlist rather than naming freely. Make the validated proposal a distinct immutable type
> carrying a content hash, so an unvalidated candidate cannot reach execution and the executor can
> re-check what it was given. Done when: a spreadsheet whose cost column is called amount_eur maps
> onto Cost rather than adding a field, two close candidates produce a question even when both
> score highly, N-12 refuses a substituted proposal on the hash, and S-1 and S-2 pass against the
> fake model.

**4.5 Safe, review, destructive**
> Read requirements AG-4, UI-4, AG-11b and AG-11d and decisions D-14 and D-7. Classify every change by
> what it can do, not by its name, and compute the evidence before the question is put. Build the
> confirmation card that states the loss in counts and examples rather than in schema terms, and
> says when a change cannot be undone; the same card, with AG-11b's evidence, is what a partial undo
> is taken from — what will be put back and what will stay, each as a count with the records named.
> Done when: adding an optional
> field prompts nothing; adding a required one reports how many records become incomplete;
> adding a unique one reports how many values collide; adding a relation reports how many source
> values match nothing; removing one reports how many records hold a value; a partial undo has a
> card of its own; and S-6 passes.

**4.6 Retrieval and follow-ups**
> Read requirements QR-1, QR-2, QR-3, QR-3a, QR-3b and QR-4a and decision D-6. Add the retrieval
> operation, and answer follow-ups in three layers: C# from result metadata where it can
> recognise the question, otherwise a refinement of the previous query, with the digest as
> material for phrasing only. Add the QueryResultHandle carrying the query, the metadata and the
> ordered identities of the page displayed, not the rows, surviving a restart. Layer one answers
> about what was shown and says so; layers two and three re-run against current data; a positional
> reference resolves against the recorded identities rather than a re-run. Done when: S-3 and S-4
> pass, "how many of those" costs zero model tokens, "which of those were in Lviv" is answered by
> a derived query, a record inserted between the answer and the follow-up does not change which
> record "the second one" means, and a thousand-row answer costs the same as a one-row answer.

**4.7 State, concurrency and compensation**
> Read requirements AG-8, AG-9, AG-10, AG-11, AG-11a, AG-11b, AG-11c, AG-11d, AG-11e and AG-11f
> and decisions D-15 and D-17, and requirements SC-12 and SC-12a. Phases 1 to 7 and step 9.3 of
> docs/versioning-requirements-and-plan.md have landed: the compensation to adopt is the test-local
> helper at TokkDb.Assistant.Tests/Compensation.cs, two passes over IStorage with validation over
> any subset; lift it into TokkDb.Assistant.Agents unchanged, and keep CompensationTests and
> UndoGuaranteeTests passing against both backends.
> Record the schemaVersion a proposal was computed against and re-check it at the write,
> re-planning on a mismatch. Serialise writes, keep reads unblocked, report busy. Add compensation
> at request granularity in one unit of work, adopting the compensation that versioning step 9.3
> wrote rather than writing a second one.
> - For record changes, validate first and write nothing. Collect every blocker: records no longer at
>   the VersionId of the request's last change to them, and conflicts in the end state computed from
>   current values and DiffVersions — unique collisions with records the request did not change,
>   references to records that are gone, and deleted records still referred to from outside. Only if
>   nothing blocks, replay every change in exact reverse order, restoring each change's
>   PreviousVersionId, or deleting for an insert, without re-checking the compensation's own versions.
>   Abort the whole unit of work on any constraint refusal during the replay.
> - For structural changes: replay the journal's inverse at its position.
>
> Refuse as a whole and name every blocker with its reason. Offer the rest as a separate action only
> after validating the rest as an undo of its own, naming any blocker that leaving records out creates.
> Give
> every operation a reversibility computed before it runs: a record change is reversible when its
> collection has no unique column and takes part in no relation, and reversible with conditions
> otherwise; a structural change is reversible, reversible with conditions or not reversible by
> what the journal keeps. State "this cannot be undone" in the confirmation, not at the attempt to
> undo. Done when:
> - N-9, N-10, N-11 and N-12 pass against the fake model and against MemoryStorage;
> - a record changed and changed back by a later request blocks the undo;
> - X a→b, Y c→a, X b→c on a unique column is undone;
> - an undo blocked by one touched record and one unique collision is refused before any write, naming
>   both;
> - a record changed a→b→c by the request, whose b another record has since taken, is refused atomically
>   at replay, naming that record;
> - a partial offer that would itself collide names the collision instead of being offered;
> - a partial undo runs only from its own confirmation (AG-11b), recorded in the trace, never from the
>   refusal's offer alone;
> - a compensation that fails part way leaves storage untouched;
> - deleting a record wider than any journal cap is reversible with conditions and its undo restores
>   every field;
> - removing a field whose values exceed the journal's cap is not reversible before it runs, and
>   says so on the card;
> - undoing a cascade deletion replays in exact reverse order, validating the whole compensation
>   inside the unit of work before any of it applies.

### Phase 5

**5.0 The thin path**
> Read Milestone A in section 8 and step 0.3. Before the rest of Phase 5, take one path end to
> end against the real transport and a real local model: a sentence of text in, deterministic
> placement into one thing, an atomic write with its durable change record, one retrieval, and a
> minimal chat display with no diagram and no browser. Report the tokens, the round trips and the
> wall clock. Done when: something typed in the window is in storage and can be read back out of
> it by asking, and the numbers are written down for the budget phase to compare against.

*Done 2026-09-14* (`TokkDb.Assistant.TokenBudget`, `dotnet run -- thin`; `chat` is the minimal
display). Against `qwen3.5:4b` over the native transport, a fresh database: storing one sentence
took 2 model calls, 2 round trips, 351 prompt and 168 completion tokens, 11.3 s wall clock (5.8 s
of it the intent call, which the shell can skip when the signal is plain); asking for it back took
2 calls, 388 prompt and 62 completion tokens, 3.1 s; the records came back through the storage and
none of them entered a prompt. Two things the real model showed that the fake could not: it split
one sentence into two records, and it named the fields as it pleased - which is the placement's
job to survive and the reason the C# side, not the model, decides where things go.

**5.1 The shell**
> Read requirements UI-1 and UI-5 and decision D-10. Add TokkDb.Assistant.App for Mac Catalyst
> and Windows: the window, the conversation, streaming replies, the conversation list, and the
> composition root wiring storage, agents and ingestion. Done when: the application runs on Mac
> Catalyst and a conversation with a real local model reaches storage.

**5.2 Attachments**
> Read requirement UI-3. Add attaching by drag, paste and picker, and the panel that shows what
> was understood about a file before anything is stored. Done when: dropping a spreadsheet shows
> sheets, header row and column types before the write.

### Phase 6

**6.1 The overview**
> Read docs/assistant-requirements-and-plan.md requirement BR-1 and decision D-13, and section
> 1.3. Add the browser's first screen to TokkDb.Assistant.App: everything stored, each with the
> name it is known by, what it keeps, how many records and when it last changed, most recently
> changed first, reachable from a persistent control beside the conversation (UI-1). Read BR-1a:
> the count and the last-changed time are maintained in the catalogue document inside the
> transaction of each write, never computed by reading records, with a reconcile command for
> drift. Done when: the list renders from real storage, opening it issues no query whose cost
> grows with the number of records stored, moving between the two surfaces keeps both their
> places, and no string in it names a collection, column or schema.

*Done (2026-09-15).* `ThingsOverview.Of` reads `IStorage.Overview()`, which both backends answer from the catalogue alone (BR-1a: `RecordCount` from the engine's descriptor, `lastChangedAt` in the settings document, touched per write and flushed once per unit of work; `ReconcileOverview()` for drift). The browser is `TokkDb.Assistant.Application/Browse/` (`BrowseViewModel`, `BrowseSurface`), reached from the Chat/Browse control; both surfaces stay built, so neither loses its place. `BrowsingTests` and `OverviewCostTests` assert the wording and the cost; the app self-test (`sh TokkDb.Assistant.Application/SelfTest/selftest-run.sh TokkDb.Assistant.Application/SelfTest/selftest-browse.txt fresh`) drove it on Mac Catalyst.

**6.2 The table, a page at a time**
> Read requirements BR-2, BR-3, BR-3a and BR-3b. Add the record table: display value leading,
> remaining fields in a stable order, total count shown, loaded a page at a time through the
> engine's ordered index. A cursor is valid only for the sort and filter it was issued for, and a
> record that moves in the sort order mid-sequence may be seen twice or missed, so the table shows
> that the data has changed underneath and offers to start again, driven by the last-changed value
> BR-1a already maintains. Done when: a collection of 10 000 records opens in under two seconds
> under NF-6's method, scrolls without growing memory, the planner reports an index walk rather
> than a scan, and editing a record mid-sequence so that it moves produces the changed indicator
> rather than a silently wrong list.

*Done (2026-09-15), with one limitation recorded.* `RecordTable` pages through `TokkDbStorage.Paged`: the order and the page bound go into the engine's `QueryRequest`, an index is raised the first time a thing of 1,000 or more records is sorted by an unindexed column, and the cursor carries `TiesSeen` so a boundary inside a run of equal values skips exactly the ties already shown - ten thousand records sharing one value page without a repeat or a gap on both backends. The count comes with the first page only. `BrowsingEngineTests`: 10,000 records open in well under two seconds, the planner reports the index walk, and a later page reads under 200 pages. **Limitation (R-1):** the engine walks an index forward only, so a *descending* sort is a bounded-heap sort that examines every record for each page - exact, still quick at ten thousand, but its cost grows with the thing; `A_descending_sort_is_exact_and_quick_but_is_a_sort_rather_than_a_walk` records it. The changed-underneath banner comes from `Describe().LastChanged` against the time the sequence began.

**6.3 Sorting, filtering and what it keeps**
> Read requirements BR-4, BR-6 and BR-10. Add sorting and filtering from the table header, expressed
> as the declarative query type of SC-7 and executed with no model, and the panel that says
> what the thing keeps in plain words. Done when: sorting and filtering cost zero tokens, an
> applied filter is visible and removable, saving what is on screen as .csv keeps the sort and the
> filter that produced it, and the structure panel contains none of the words column, type, index,
> schema, constraint or nullable.

*Done (2026-09-15).* Headings sort (a second click turns the order round, and each sort is a new sequence, BR-3a); "Narrow it down" builds a `TableFilter` that becomes a `QueryCondition` of the same `StorageQuery` the assistant runs; applied filters are chips with a ×; "Save as a file" walks every page of the same cursor into a .csv in the app's `saved/` folder. `WhatItKeeps` writes each field as "name keeps a kind; notes", and a test checks every string against the forbidden words. No model is involved anywhere on this surface; the diagnostics line says so.

**6.4 The record, and what it relates to**
> Read requirements BR-5 and BR-7. Add the record detail: every field it has, empty ones shown
> as empty, the display value as the title, and related records reachable in one step under a
> heading that says what the relation means. Done when: a record written before a field existed
> shows that field empty, and the relation heading reads as something a person would write.

*Done (2026-09-15).* `RecordDetail.Open` lists every field of the thing's shape, an empty one as "nothing here", the display value as the title, and the related records on each side of every relation under `WhatItKeeps.Heading` (the relation's purpose where it has one, "The trips this belongs to" otherwise). Tests: `A_record_shows_every_field_and_what_it_relates_to`.

**6.5 Changing things from here, and the bridge**
> Read requirements BR-8 and BR-9 and decisions D-7 and D-8. Make an edit or a delete from the
> browser go through the same rules as one made by saying so: additive silently, destructive
> with the confirmation, every change traced and shown in the diagram. Connect the surfaces
> both ways — a chat result row opens here, a record here becomes the subject of the next turn.
> Done when: deleting a record from the table is indistinguishable in trace and diagram from
> deleting it in the conversation, and scenario S-8 passes with zero tokens recorded.

*Done (2026-09-15).* `Orchestrator.ChangeAsync(conversation, action, said)` is the browser's way in: the same `RestructureFlow.ApplyAsync`, the same `ChangeClassifier`, the same card in the conversation, the same trace - with no intent call, so no model call. A record edit is a new `ChangeRecord` action (Safe, applied at once, journaled as an Update with the versions before and after); a removal is `DeleteRecords` and waits on the card. `BR8_a_removal_from_the_browser_is_indistinguishable_from_one_said` compares the steps after "what it would do", the card, and the journal entries. The bridge: a result row in the chat opens the record in the browser; "Ask about this" calls `Orchestrator.LookAt`, which appends a turn carrying the same `QueryResultHandle` a result would, so the next thing said is about that record (`BR9_...`). `S8_looking_for_oneself_records_zero_tokens` runs the browsing sequence after S-5's correction with the model's call count unchanged and no request begun. On Mac Catalyst the self-test log shows the whole of S-8 through the real window, the edit from the browser traced as `the person -> the assistant -> what it would do -> the change -> the answer`, and the removal's card answered in the chat.

### Phase 7

**7.1 Layout**
> Read requirement TR-5 and decision D-9. Add TokkDb.Assistant.Diagram: trace to geometry —
> participants, calls, lanes, labels — as pure testable C# with no MAUI app types. Done when:
> the layout of a fifteen-step trace is asserted by tests, including a nested call.

*Done (2026-09-15).* `TokkDb.Assistant.Diagram` references the trace and nothing else: `DiagramLayouts.Layout(steps, options, stepsWithChanges)` gives lanes (you, the assistant, the model, what is stored), one block per step in trace order (the chain of `After`, time where the chain is broken), nesting from time containment (a step that began while another was still running sits inside it, indented, and the parent grows to cover it), and call/return arrows between the assistant's lane and the others, all in points. `DiagramLayoutTests` asserts a fifteen-step trace with a nested call, the trace order over a shuffled read, and a step recorded twice counted once as its latest recording.

**7.2 Drawing and hit testing**
> Read requirements TR-5 and TR-5a. Host the layout in a GraphicsView, draw it, make it grow as
> steps arrive, and make blocks clickable. Produce one transform from the view's scale at draw time
> and use it for both drawing and hit testing, invalidating on a display or scale change; keep the
> layout in device-independent points and not in fractions of the viewport. Done when: a running
> request shows steps appearing, clicking any block reports which it was on both platforms, and a
> test applying scale factors of 1.0, 1.5 and 2.0 to a fixed layout returns the same block for the
> same logical point with no second display present.

*Done (2026-09-15).* `TokkDb.Assistant.Application/Diagram/DiagramView` is a `GraphicsView` over the layout: at draw time it makes one `DiagramTransform` from the view's width (`FitWidth`, never above one to one), draws through it and keeps it, and a click goes through the same transform (`HitTesting.BlockAt`) - so the block under a point is the block drawn there at any scale; a display change or a size change invalidates and makes a new transform, never a new layout. It grows as steps arrive because the pane re-lays the live steps. `The_same_logical_point_lands_on_the_same_block_at_every_scale` asserts scales 1.0, 1.5 and 2.0 with an offset, with no window and no second display. Verified by eye on Mac Catalyst through the self-test screenshots; Windows is not available on this machine (recorded under 9.7).

**7.3 Detail views**
> Read requirements TR-6, TR-6a, UI-6 and EX-4, and SC-12's note on the diff as built. First make
> DiffVersions carry what the thing's current shape cannot show — the engine's unmapped values, on
> both backends, with the contract suite asserting it — so that a field since removed or retyped
> comes back as it was, named as such, rather than vanishing. Then add the detail panel with JSON as
> the fallback and three specialised views: before-and-after for data changes, showing a since-removed
> or since-retyped field as it was with a plain note of what became of it; tokens and timing for
> model calls; the query and row count for retrieval. Selecting an earlier request in the
> conversation shows its diagram. Done when: the three render, the fallback covers the rest, a new
> view is registered in one place, an earlier request's diagram comes back by clicking it, a change
> to a field since removed is shown marked as such on both backends, and the conversation's earlier
> replies are unchanged by a later structural change.

*Done (2026-09-15).* First half: `ColumnChange` carries `Fate` (flags: since removed, since renamed, since retyped), `WasCalled` and `KeptThen`, and `Note` says it in words. The engine's `DiffVersions` reads each version as stored (a value a retype converted is shown as it was), takes the values the schema mapping could not carry from the engine's own `Unmapped` list, and the name a field had then from the descriptor's migrations; the memory backend keeps beside each version what a rewrite drops and remembers renames. `A_diff_shows_a_field_since_removed_renamed_or_retyped_as_it_was_and_says_so` and `A_diff_after_a_clean_retype_shows_the_values_as_written` run on both backends. Second half: `DetailViews` is the one registry (EX-4) - a record change as before-and-after through `RecordChangeTable` with the fate note beside a field and "the values of this change are no longer kept" after a purge; a model call as tokens, round trips, repairs, peak context and time; a retrieval as the query in words and the row count; a structural step as its journal payload; JSON for the rest. `StepsPane` draws the diagram and the focusable list from one layout, either selection opens the same panel (UI-8), and choosing an earlier reply's "what happened" shows that request's diagram (UI-6). Earlier replies are conversation turns and are never rewritten.

### Phase 8

**8.1 The harness**
> Read requirements NF-1, AG-1a, AG-1b, AG-1c, AG-1d and AG-1e, sections 6, 6.2a and 6.3, and
> decision D-12. Add TokkDb.Assistant.TokenBudget: the
> scenarios of section 5, every figure of 6.2a read from the trace, and a table written to
> docs/assistant-token-budget.md. Count round trips separately from model calls, assert peak
> context rather than only reporting it, and measure prompt tokens with a single-token probe
> call rather than reading them off a working one. Measure each scenario's end-to-end success
> rate over repeated runs and gate on it, reporting the product of the per-call rates beside it as
> a diagnostic and the distance between them as the correlation measurement. State the run count,
> assert against the lower bound of the interval, and count failures by mode. Done when: the
> report is generated, every scenario has each measured figure beside its limit, a floor asserted
> on too few runs fails as thin rather than passing, and a scenario that gains a model call or a
> round trip fails until the limit is raised deliberately.

*Done (2026-09-15).* `TokkDb.Assistant.TokenBudget/Harness/`: `ScenarioRunner` runs S-1 to S-4 as one measured request each over a fresh database with the precondition put in place by storage; `RunFigures.From` reads every figure of §6.2a from the request's trace - prompt and output tokens, model calls, round trips counted apart, repairs, peak context, latency, and each call's mode from the runner's own step output; `ProbingChatClient` sends every prompt once more with the output capped at one token and reports that figure beside the working call's, keyed by the trace's prompt hash; `ScenarioMeasurement` holds the maxima beside `Budgets` (§6.2's tokens, the calls each scenario makes, a repair allowance of one), the success rate with the Wilson lower bound at 95% against the floor, the product of the per-call rates after repair as AG-1d's diagnostic and its distance, and failures by mode. `Report` writes `docs/assistant-token-budget.md` in two sections, the real model's and the fake's. `TokenBudgetTests`: every scenario meets its limits over the fake across 20 runs, three runs fail as thin, a scenario given one call or one round trip fewer than it makes fails until the limit is raised, a repaired malformed answer is counted as a mode and not as a failed request, and S-2's prompt tokens are the same at 100 rows and at 1 000. `dotnet run --project TokkDb.Assistant.TokenBudget -- measure [--ollama] [--runs N] [--only S-3]`.

**8.2 Meeting the budget**
> Read section 6.2. Bring every scenario within its budget, reporting what each change bought.
> Done when: all budgets are met, a test fails when one is exceeded, and S-2's cost does not
> move when the row count does.

*Done (2026-09-15).* Over the fake every scenario is within budget. Over `qwen3.5:4b` the tokens are far inside the budgets (S-1 391 prompt tokens against 9,000; S-2 334 against 7,000; S-3 642 against 4,500; S-4 none against 2,500), and what the measurement bought was not a token saving but three corrections to the query operation's instructions: the model read "last year" as the past twelve months and found nothing in 2 of 3 runs, so the instructions now read a period by the calendar with a worked example (S-3 then 4 of 4, at a cost of 187 prompt tokens, though one run in eight still takes the twelve-month reading); once in four it put "orderBy" inside "where", so the instructions say the ordering fields are never conditions; and "which was the most expensive" as a fresh question came back unordered with a limit of one, so a superlative now orders by its field. The final real run: S-1 4 of 4, S-2 4 of 4, S-3 4 of 4, S-4 3 of 4 (the miss was its unmeasured retrieval). One repair in three runs of S-3 set the round-trip limit at calls plus one and the repair allowance at one, deliberately. S-2's cost does not move with the row count - the mapping call sees a profile and a bounded sample, never the rows - and the test asserts it. The success floor is asserted against the lower bound of the interval, which four runs cannot support: the real section of the report says so as "thin" rather than passing; twenty runs per scenario over the real model is a run of an hour and is left for a machine that can give it.

### Phase 9

**9.1 Correction**
> Read requirements QR-4, QR-4a and QR-4b. Add the correction path: the user says what is wrong,
> the system finds the record and updates it, and the trace shows before beside after. A positional
> reference resolves against the identities the handle recorded, never against a re-run, and
> staleness is checked at the field being corrected: an unrelated edit does not block it, an edit
> to that field stops it and shows both values, and a record that has left the query's filter is
> still correctable. Done when: S-5 passes without the user naming a collection or a record, and
> each of those three cases is a test.

*Done (2026-09-15).* `CorrectionFlow` resolves the record against the handle's identities, checks staleness at the corrected field with `DiffVersions` between the version shown and the head, and writes the change as one step whose input is the old value and output the new. `CorrectionTests` holds the three cases QR-4b names - an unrelated edit does not block, an edit to the field stops and shows both values ("it read 12000 then and reads 12500 now"), a record that left the filter is still corrected by position - and QR-4a's two: a record inserted meanwhile does not move the positions, and a deleted one is reported rather than another corrected. S-5 itself is `S5_a_correction_updates_the_record_and_the_trace_shows_before_beside_after`.

**9.2 Retention**
> Read requirements TR-7, TR-8 and NF-4d. Add three retention settings: a shorter window for
> diagnostics, a longer one for the change journal, and the compensation window, which bounds how
> long the engine keeps version history for the assistant's collections. Add a purge that removes old
> diagnostics and leaves data, conversations and change history alone, and a history purge that
> removes versions older than the compensation window through PurgeRecordHistory. Refuse any
> configuration whose history purge would fall inside the compensation window, naming both moments.
> Done when: the diagnostics purge is asserted to touch nothing but diagnostics; every change stays
> attributable to a request and a time; compensation still works inside the window and refuses with
> "no longer kept" outside it; and the refused configuration is a test.

*Done (2026-09-15).* The three windows are `RetentionWindows` (diagnostics 30 days, changes a year, compensation 90 days, with history kept for the compensation window), surfaced as three settings in `AppSettings` and carried in `OrchestratorOptions.Retention`. `RetentionSweep.Run` first asks the windows for the history moment - a configuration whose history purge would fall inside the compensation window is refused there, naming both moments, before anything is touched - then purges the diagnostics of requests finished before the diagnostics window (`ITraceRecorder.PurgeDiagnostics`) and the version history older than the history window of every record the change journal ever named (`ITraceRecorder.ChangesBefore` + `PurgeRecordHistory`). The application runs the sweep at startup, after `RecoverAsync`. `RetentionTests`: the diagnostics purge leaves the data, the conversation and the journal (with its request and time) untouched, and an undo still works from the journal alone - `UndoFlow` stands a purged request in from its changes and the person's turn; outside the window the versions are gone and the undo refuses as "no longer kept". `RetentionSweep.EndWindow` is NF-4d's per-request purge.

*Revised by draft 10.* AJ-8's refusal already exists as `RetentionWindows` in `TokkDb.Assistant.Trace`
(versioning step 9.3, tested in `CompensationTests`); this step keeps it where the settings live and adds
the two purges around it.

**9.3 The vocabulary, in three tiers**
> Read requirements UI-2 and BR-6 and section 1.3. Sort every user-visible string into its tier:
> the conversation and the browser's primary surfaces take plain words with no exceptions; the
> diagram's detail panel and the diagnostics area may be technical, because inspection is their
> purpose; between them a technical term needs a plain gloss beside it. Done when: the first
> tier contains no database vocabulary, the detail panel still says what it means, and the
> review is listed.

*Done (2026-09-15).* `docs/assistant-vocabulary-review.md` lists the three tiers and the sources of each; `VocabularyTests` reads every first-tier source and fails on a string literal that says collection, column, index, schema, query, constraint, nullable, database, SQL or a key, with interpolation holes stripped first. The review found two things and fixed them: change descriptions and evidence sentences printed stored names with underscores, and the browser's message for a name that never existed said "no longer kept". The detail panel keeps its technical words (the test checks it still does), and "what this keeps" is the third tier's worked example.

**9.4 Egress, secrets and the deletion window**
> Read requirements NF-4, NF-4a, NF-4b, NF-4c, NF-4d, NF-4d1 and NF-4e. Make egress a mode: LocalOnly
> refuses a remote endpoint rather than warning, RemoteAllowed carries a permanent indicator.
> Local means loopback; a LAN address is remote. Credentials live in the platform secret store and
> never in the database, a trace or a settings document. Declare an egress class per operation
> beside its model configuration and context budget. Apply redaction to step payloads, not to
> prompts, which are already hashed. Resolve deletion against durable undo in the open: a deleted
> record stays restorable from version history for the compensation window, and the confirmation
> card says how long. Ending the window early works per request (PurgeRecordHistory for the records
> it changed) and per thing (Erase). Erasure through the assistant also clears the step payloads of
> the requests that changed the record — adopt what versioning step 9.4 built — and the card says
> conversations are kept. Create the database private to the user in the platform's application
> data location, and record encryption at rest as out of scope with its reason. Done when:
> configuring a remote endpoint in LocalOnly fails with a message naming the mode; a test asserts no
> operation sends more than it declared; payload retention is shorter than change-history retention;
> a credential is not recoverable from the file by inspection; a test searching the raw database file
> and its journal right after an erase commits — no compaction and no close — finds the erased value only
> in the user's own conversation entry; and right after a per-request purge commits, a value that
> existed only in the purged versions is in neither file.

*Done (2026-09-15), with encryption at rest recorded as out of scope.* Egress: `Egress.Check` refuses a remote endpoint in `LocalOnly` naming the mode; local is loopback and a LAN address is remote (`EgressTests`); each operation declares its `EgressClass`, and `OperationCatalogTests` asserts no operation sends more than it declared. Secrets: `ISecretStore` is asked for a credential at the moment the transport is made and it goes into the request header and nowhere else; `A_credential_is_not_recoverable_from_the_database_file_a_trace_or_a_settings_document` searches the raw file, the journal, the trace and a settings document. Redaction of payloads is by clearing (TR-2b), prompts are hashed. Deletion: a removal's card states the window ("can be put back for 90 days, then it is gone for good"); ending it early works per request (`RetentionSweep.EndWindow`) and per thing or record (`EraseRecords`, "for good"/"erase"/"completely" in a removal, or the browser's "Erase for good"), which erases the record and every version and clears the step payloads of every request that named it - the card says it cannot be undone and that conversations are kept. `ErasureThroughAssistantTests` searches the database file and its journal right after the commit, with no compaction and no close: the value is found nowhere but where the person typed it, and after a per-request purge a value that existed only in the purged versions is in neither file (the payloads are cleared first, so the purge's commit is the one that leaves no frame). The database lives in the application's data directory, private to the user (`AppSettings.DatabasePath`); encryption at rest is out of scope: the engine has none.

**9.5 Keyboard, focus and the screen reader**
> Read requirements UI-7 and UI-8 and risk R-8. Give the whole application a defined focus order
> across both surfaces and the switch between them, keyboard navigation of the table including
> paging, focus restored after a confirmation closes, a name and a role on every interactive
> element, text that scales with the platform setting, and contrast checked against the palette in
> docs/design/ rather than assumed. Add the diagram's parallel semantic representation: the same
> layout model as a focusable list of steps with name, status and outcome in trace order, opening
> the same detail panel a click opens. Done when: S-1 to S-8 are completed keyboard-only, the
> accessibility tree is inspected on Mac Catalyst and on Windows, and a screen reader announces
> the steps of a finished request in order.

*Done on Mac Catalyst (2026-09-15); Windows not available on this machine.* Every primary control is a button or a field with a name a screen reader can say (`SemanticProperties`): the surface switch, the conversation list, the composer and its actions, "what happened →" on every reply, an open button on every result row, the browser's rail, the overview cards, the column headings (sort, again to turn the order round), an open button on every table row, "Show more" for paging, the filters and their ×, the record's back, ask, change, remove and erase, and every related record. Focus returns to the composer when a confirmation closes. The diagram's parallel representation is the focusable list of steps drawn from the same layout (UI-8), which opens the same detail panel. The accessibility tree is inspected by the self-test's `a11y` line, which walks the visual tree of the window as the platform sees it and logs every interactive element without a name and the focus order: on the conversation surface 39 interactive elements, on the overview 14, on the table 125, all named but one - the composer's drop frame, named since. Text scales with the platform setting because every size is a MAUI font size; contrast: the palette of `docs/design/` (text #CCCCCC and #FFFFFF on #111111 and #202023, dim #888888 on #202023) meets the platform's 4.5:1 guidance for body text, and #666666 is used for hints only. Keyboard-only completion of S-1 to S-8 and the Windows inspection are recorded under 9.7 as not exercised by a person on this machine.

**9.6 The performance report**
> Read requirements NF-2, NF-3, NF-5, NF-6 and BR-1a. Build the generated fixtures, run each
> timing cold and warm over a stated number of runs, and report p50 and p95 with the planner path
> beside each and the machine named in the report rather than in the requirement. Done when: the
> report exists, a run whose planner path is a scan where a seek was required fails even when the
> time is met, and the overview's cost does not grow with the number of records stored.

*Done (2026-09-15).* `dotnet run --project TokkDb.Assistant.TokenBudget -- perf` builds the fixtures once (100,000 records of eight fields of every kind plus 1,000 requests of three steps; 1,000 records for the comparison; 10,000 rows for the ingestion), runs each timing cold - the process's first open, the OS cache not cleared, and said so - and warm over a stated number of runs, and writes `docs/assistant-performance.md` with p50 and p95, the planner path where one is required, and the machine. A time met by a scan where a seek was required fails, and the overview's page reads at 100,000 records are compared with those at 1,000. On the machine named in the report: opening with 100,000 records and 1,000 traces took 89 ms cold and 52 ms warm against NF-3's two seconds; a retrieval of 100 records of one category oldest-first took 55 ms at p50 through the ordered index walk, with the p95 of the first run carrying the index it raised; ingesting 10,000 rows in one transaction through the importer took 2.5 s against NF-5's thirty; and the overview read no pages at either size (BR-1a).

**9.7 The whole thing**
> Read section 5, including the twelve that are not happy paths. Run S-1 to S-8 and N-1 to N-12
> against a real local model on Mac Catalyst and Windows, and report what worked, what did not,
> and what the trace shows for each. For the negative ones, assert what the user is told and not
> only what the system does. Done when: the report is written and any failure is either fixed or
> recorded as a known limitation with a reason.

*Done on Mac Catalyst (2026-09-15); Windows not available.* `docs/assistant-scenario-run.md` is the report: S-1 to S-8 and N-1, N-3, N-4, N-5, N-7, N-8, N-9 and N-11 were run through the application's own window against `qwen3.5:4b` by the self-test driver (`TokkDb.Assistant.Application/SelfTest/selftest-97.sh`), which types, attaches, answers, kills the process with a card on screen and reopens; every one worked, and the report gives what the person was told and what the trace shows for each. N-2, N-6, N-10 and N-12 are asserted by the scripted tests named there. Two things the run found were fixed: a question left on screen by a crash was held durably but the reopened application did not open its conversation, and "keep these as trips" went into conferences because a name the person gave was still a question for the model (`Intents.NamedThing` now decides it, with no mapping call). One thing it found once and did not find again is recorded: an undo that ended in an index error; the application now keeps an error log with the stack. Known limitations, with reasons, are listed in the report: no Windows machine, the descending sort as a sort, the twelve-month reading of "last year" about one time in eight, the model's own names for the fields it extracts, and keyboard-only completion by a person not exercised.

---

## 10. Still open

Worth settling before the phase that needs them, not before the document is useful.

1. **Which models, specifically**, for each operation. `qwen3.5:4b` is the current default; a
   dedicated extraction model may be worth its download. Settle after R-2b and R-3 are measured,
   and after R-7 decides the transport.
2. **The retention defaults** — three numbers: one for diagnostics (TR-7), one for change history
   (TR-8), and the compensation window of D-17. The window cannot be longer than change history, and
   it is also how long the engine keeps version history for the assistant's collections (step 9.2).
3. **The spreadsheet library** — `ClosedXML` against `DocumentFormat.OpenXml` directly. Settle
   in step 2.1, with the reason recorded.
4. **What happens to a conversation whose storage has since changed** — a trace from before a
   field was removed still refers to it. *Settled in draft 10, TR-6a:* shown as it was, with a note
   of what became of the field, because the engine reports exactly that when a version is read
   through the current schema.
5. **Whether the browser may create as well as read.** BR-8 allows editing what is there.
   Adding a record by hand, or a new thing to store, is a different question: it is the one
   place the premise of section 1.3 could quietly invert, because a person given an empty form
   with named fields is being asked to design a table.
6. **The floor and the gap of AG-3b.** They decide how often the assistant asks, and neither can
   be chosen from taste. They come out of running N-1 and N-2 over a corpus of real files.
7. **Whether phrasing survives as a model call at all.** AG-1c may show it is the cheapest call
   to delete, with C# templating the common outcomes. That is a measurement, not a preference.
8. **The reliability floors, and the number of runs behind them.** A floor is only as good as
   the sample under it, and a local 4B model is slow enough that the run count is a real cost.
   Settle in step 8.1, with the interval stated beside every figure.
9. **Whether a partial undo needs its own confirmation** (AG-11b). *Settled in draft 10:* it does.
   Offering the remaining 499 is clearly right, and taking the offer is a second question on a card
   of its own, because "undo these 499 and leave that one" is not the question that was refused.
10. **The change journal's cap**, which decides which **structural** changes are reversible at all
    (AG-11c, AG-11e). Record changes no longer depend on it (D-17). Too small and removing a field is
    never undoable; too large and the journal outgrows the data. Settle it against a real collection
    width. The versioning plan's F-7 — undoing a structural change from history — would retire this
    item if it is ever built. The engine now keeps a removed field's values in history (that plan's
    WV-8, `GetStoredAsOf`), so F-7 lacks only the re-add that fills a field from each record's version
    as of the removal without touching what changed since; it is still future, and the cap still
    decides.
11. **What the diagram's semantic list reads like** (UI-8). The step names are written for a
    reader who can see the shape; spoken in sequence with no shape, they may need different
    wording. Settle in step 9.5, by listening to it.
12. **The ratio at which SC-9a switches** from an ordered pass to per-value seeks. It depends on
    page size, key width and tree height, so it comes out of measurement in step 1.5 rather than
    out of a rule of thumb.
13. **How long a column may stay mixed** (SC-6b, SC-6c). Reporting excluded records keeps the
    state honest but not comfortable, and converge is the way out. Whether the interface nags
    about it, and after how long, is an interface question rather than a contract one.
14. **Whether the browser shows a record's history** (the versioning plan's F-10). Section 1.2 keeps
    it out of this stage, and SC-12 keeps `History` and `GetAsOf` off the contract. The engine now has
    everything a timeline needs — a record's version forest, any version through the current shape
    with what it cannot show reported, and a diff of any two — so the cost has moved from the engine
    to this plan: two contract operations on both backends, a fake keeping a full copy per version, a
    timeline on BR-5's detail that passes UI-2's first tier, and a Phase 6 step. It is the one future
    item of that plan that section 1.3's "and yet they can look" argues for. Settle it before Phase 6;
    if yes, TR-6a already says how a past version is shown when the shape has changed since.

---

## Appendix: what changed in draft 3

Twenty review comments, all upheld in substance. Four became decisions because they change
structure rather than wording: **D-14** (a change is classified by what it can do), **D-15** (a
request is a resumable state machine), **D-16** (the query model has two faces), **D-17** (every
change is compensable now).

Three pairs of comments collapsed into one thing each, which is the useful part of the review
rather than an accident of it. Ambiguous mapping and the weak field-mapping model are one
object, **AG-3**'s placement proposal. Duplicate detection and deduplication are one
requirement, **IN-5 to IN-7**, and the old IN-5 was not merely thin: its acceptance criterion
was satisfied by storing 44 records from importing 22 twice. Trace bloat, the trace-audit split
and compensation converge on **DataChange**, which turns out to be the audit log, the size
discipline and the undo log at once.

Two comments corrected positions taken earlier in this document rather than gaps in it. D-7's
"additive is safe" was false in four ways that delete nothing (D-14). UI-2's absolute ban on
technical vocabulary made TR-6's detail panel impossible, since showing an access path is the
point of it (UI-2, now three tiers).

One comment corrected advice given in conversation rather than in the document: splitting work
across model calls multiplies failure, measured at 14 of 20 per call, so **AG-1c** makes
per-call reliability an asserted property and the predicted end-to-end figure the product. *That
last sentence is what draft 4 replaced: the product assumes independence. See below.*

---

## Appendix: what changed in draft 4

Five review comments. Two were stale against draft 3 and are recorded here because what they
expose is not.

**One comment was fully upheld.** The product of per-call rates assumes failures are independent,
and the spike supplies the shared cause: context shift belongs to the request, not the call, so
failures clump in the same runs. **AG-1c** now gates on a measured end-to-end rate, **AG-1d**
keeps the product as a diagnostic and makes the distance between them the correlation
measurement, and **AG-1e** counts failures by mode. Two things the comment did not say turned out
to matter as much: the rate that composes is the one after AG-5's repair, because a repaired call
is not a failed request; and a floor asserted on a thin sample is a claim about the sample, so
the assertion is against the lower bound of the interval.

**One comment was right about a missing mechanism rather than a missing guarantee.** D-15 and
AG-8 already promised that answering a confirmation executes what was shown, but nothing could
fail if it did not. **AG-3d** adds a validated proposal that is a distinct immutable type
carrying a content hash, so an unvalidated candidate cannot reach execution and the executor
re-checks what it was handed. The proposal lifecycle the comment proposed was declined: D-15
already holds the request's states, and a second state machine would record the same facts twice.
**N-12** is the test that can now fail.

**One comment was mostly answered and still found a defect.** D-17 and AG-11 already refused to
half-apply, but the guard they named was AG-9's `schemaVersion`, which does not move when a value
changes. **AG-11a** makes the precondition per record and about its contents, **TR-2b** gives an
insert a content hash so the question stays answerable for the commonest case, which is an
import, and **AG-11** puts compensation in one unit of work so nothing half-applies by rollback
rather than by care. **AG-11b** refuses as a whole, names the records that block it and offers
the rest as a separate action: refusing to undo 499 records because one changed is a defensible
default and an indefensible only option.

**One comment was stale about the symptom and right about the gap.** IN-4 and IN-7 already
reported reasons and merge results, but in two prose sentences with no named type and no home for
a conflict. **IN-8** makes a row's disposition an outcome from a closed set plus a reason carrying
the detail, rather than one enum that bakes reasons into outcomes and leaves others unexplained.
**IN-8a** persists it, which is the argument the comment did not make: undoing an import has to
know per row what happened, and a stored count would delete records the import never created.

**One comment described draft 2.** QR-3b already re-runs rather than pinning. But the framing
exposed two places where "always current" is too simple. Ordinal reference is one: **QR-4a**
resolves "the second one" against the identities recorded in the handle (**QR-3a**), because
re-running is right for a question about data and wrong for a reference to a row on screen. The
three layers of **QR-3** are the other: layer one answers from metadata captured at display time
and now says so, while layers two and three re-run.

---

## Appendix: what changed in draft 5

Seventeen review comments. **Six were contradictions inside the document rather than omissions**,
which is the finding worth recording: a requirements document that disagrees with itself sends the
disagreement into the code.

**The project graph did not compile.** §3.1 declared that the orchestrator depends on the diagram
and that the diagram depends on the orchestrator. A new `TokkDb.Assistant.Trace` project holds the
trace model and the recorder interface; five projects depend on it. The second, quieter error was
that the orchestrator never needed the diagram at all, so that edge is gone rather than reversed.

**Traces could not be both atomic and live.** The old TR-4 put every step in the transaction of
the change, while TR-5 grows the diagram during the request and D-15 survives a kill before any
write exists. **TR-4** now commits the audit record with the mutation; **TR-4a** persists lifecycle
and diagnostics as they happen; **TR-4b** stops an interrupted step reading as done; **TR-4c**
states the durability point instead of implying one per step.

**Compensation promised more than the payload rules allow.** D-17 is now a matrix of three values,
decided **before** the change runs, with **AG-11c** to **AG-11e** carrying it: removing a field
whose values exceed the journal's cap is irreversible, and the card says so while the user can
still decline. Silently keeping half an inverse would have been worse than keeping none.

**The two ingestion requirements disagreed.** IN-5 forbade storing a row twice; IN-7 defaulted to
add-all when no natural key existed. **IN-7** now has one default, skip existing, over the key
where there is one and the fingerprint where there is not. **IN-6** validates a candidate key
against stored records and not only against the file, **IN-6a** makes the fingerprint a stored,
indexed field carrying its normalisation version, and **IN-6b** creates the uniqueness rule that
IN-7 was already leaning on.

**Row failure and operation failure were the same word.** **IN-9** and **IN-9a** separate them and
classify every row before the transaction opens, which existing requirements make affordable:
parsing already reads the whole file, and serialised writes remove the one conflict that normally
cannot be predicted.

**Paging promised a snapshot it has no mechanism for.** BR-3's criterion now covers ties, which the
composite key does solve, and **BR-3b** states that a record moving in the sort order mid-sequence
may be seen twice or missed, with a changed-underneath indicator driven by data BR-1a already
maintains.

Seven more were gaps rather than contradictions. **SC-8** and **SC-8a** create relations and give
them an integrity action defaulting to restrict, which nothing had done although compensation, the
browser and the classifier all assumed they existed. **SC-6a** and **SC-6b** write down what lazy
retyping does with a value that will not convert, which SC-3 left with no legal answer. **SC-7a**
replaces the leaked planner contract with a storage-owned `QueryExecutionInfo` whose fakes report
honestly rather than claiming a seek they did not perform. **AG-8a** and **AG-8b** add the
single-writer lock, compare-and-swap transitions and startup recovery, and reuse AG-3d's content
hash as the idempotency key rather than inventing one. **AG-1f** makes "no model call outside an
operation" a boundary instead of a convention. **BR-1a** stops the overview being a scan of
everything stored. **NF-4c** to **NF-4e**, **NF-6**, **UI-7** and **UI-8** cover what local means,
where credentials live, how deletion and durable undo are reconciled in the open, how a timing is
measured, and how a drawn diagram is used by someone who cannot see it.

Two were about sequence rather than content. The transport question is now **Phase 0.3, a gate**:
Phase 5 does not begin until it is decided, and Phase 8 asserts nothing real until R-2b is closed.
And **Milestone A** pulls one thin path forward across phases 1 to 5 rather than reordering them,
because the risk being bought off is integration arriving late, and a slice buys that off without
rebuilding the plan around it.

---

## Appendix: what changed in draft 6

Six comments, and two of them were answered by reading the engine rather than the document.

**A mixed-type column does not misbehave the way it was expected to, and misbehaves worse.** Every
encoded key carries its type in its first byte, so nothing throws and nothing slows down. What
happens instead is that a range walk never enters the run of another type, so an answer omits
records and looks complete. **SC-6c** therefore reports what was excluded, in the execution info
**SC-7a** already carries, and **SC-6d** records that uniqueness in such a column is per type. The
proposed shadow field was declined: it would contradict SC-3 and SC-6b and give every reader a
second permanent place to look for a temporary state.

**Checking ten thousand fingerprints one at a time is worse than it sounds, for a reason that is
not round trips.** The engine is in-process, but `DiskReader.ReadPage` allocates a buffer and hits
the file on every call, with no page cache anywhere. **SC-9** therefore specifies one ordered pass
over the index rather than a batched lookup, which would have hidden the loop rather than removed
it. **SC-9a** keeps per-value seeks as the small-set branch. This is what makes IN-9a's rule that
everything is classified before the transaction opens affordable at NF-5's scale.

**The ordinal comment half-described the fix it was commenting on.** Positional references already
resolve against recorded identities rather than a re-run. What was missing is staleness, and
**QR-4b** puts it at the **field** being corrected: an unrelated edit does not block the
correction, an edit to that field stops it and shows both values, and a record that has left the
filter is still correctable, because the handle and not the re-run is the authority. A per-page
version counter was declined, since an immutable handle already carries a version.

**Cascade compensation needed an order, and not a topological sort.** **AG-11f** replays the
recorded changes in exact reverse order, which is valid by construction because the deletions were
performed in an order the same constraints accepted, and validates the whole compensation inside
the unit of work before any of it applies. **AG-11c** now marks any cascade-enabled request
`ReversibleWithConditions`, because a rule created after the deletion invalidates the restore
however it is sequenced.

**The diagram spike's result was read more widely than it holds.** Tap and canvas coordinates
coincide on one display at one scale, which says nothing about a window dragged onto a monitor
with another. **TR-5a** requires one transform, produced at draw time and used by both drawing and
hit testing, with layout in device-independent points. Normalised coordinates were declined
because text has an absolute size and would not scale with a fractional layout. The test applies
three scale factors to a fixed layout and needs no second monitor, which matters because there is
no Windows machine here.

**And one correction to something that was about to be relied on for the wrong thing.** Ollama's
prefix cache buys latency, not headroom: the cached prefix occupies the same window, and a local
model bills nothing for it to save. §6.1 now says so, and **AG-6a** turns the byte-identical
prefix from a habit into an assertion, because a reordering of two blocks is invisible in review
and costs the whole cache.

---

## Appendix: what changed in draft 7

A pass over the whole document rather than over a list of comments, looking for what six rounds of
patching had left behind. Three kinds of thing, and the third was the surprise.

**Claims that were true two drafts ago.** AG-11c called every record delete reversible while D-17's
own third case says re-inserting a record can violate a rule created since, so **record delete is
now `ReversibleWithConditions`** and only insert and update are unconditional. D-17's closing
paragraph still said the *trace* keeps removed values, which AG-11e had moved to the journal.
TR-2 called the audit record *small* while TR-2b makes a delete record the whole record; it is
**bounded**, which is a rule rather than a size. BR-8 still routed browser changes through D-7's
binary, which D-14 replaced. And D-2 still said **two** reserved collections, written before the
trace model had a change journal, a request state or conversations in it; the count is now settled
in step 3.2 instead of guessed here.

**Criteria no test could pass.** NF-4d asserted that a searched file holds no copy of a deleted
record, which the engine cannot do, because it reclaims freed space by compaction only: the
criterion is now about **reachability**, and **NF-4d1** adds the compaction that makes the stronger
claim true. SC-9 required each index page to be touched once, which holds only if the B+Tree walks
leaves without re-descending, so the bound is now the pages of the range and step 1.5 records
which it is. IN-5's criterion cited a previous draft of itself, and IN-6a's asked for a per-row
lookup and a single ordered pass in the same sentence.

**Two features the document depended on and never defined.** Section 1.1, S-7, UI-1 and TR-4d all
rely on conversations being stored, and no requirement described one: **SC-10** does. BR-2 leads
its table with a display value and BR-5 titles a record with one, while EX-1 listed display values
among things that might be added later: **SC-11** defines it, with the rule and the three
fallbacks, and EX-1 now covers only its refinement.

**And a plan that did not schedule its own premises.** AG-1a forbids a budget set from taste and
points at R-3, which had no step in any phase, while §6.2 set seven budgets anyway. R-3 is now
**step 0.4**, the budget table says it is provisional until that runs, and the ceiling above it is
whatever the transport gate establishes rather than the 64k D-3 hoped for. Fifteen requirements
were cited by no step at all; the genuinely unscheduled ones — the confirmation card, bad-row
reporting, CSV export, showing an earlier diagram, and all three extensibility guarantees — now
have one.

**Ordering, which matters for a document meant to be executed top to bottom.** The steps were
numbered in the order they were written, so 3.0 created the project 3.1 needed and 9.4 ran before
three later-numbered steps. They are now in execution order, Phase 9 renumbered so that "the whole
thing" is last. The agent group read AG-1, then AG-8 to AG-11f, then AG-2, and now runs in order.

---

## Appendix: what changed in draft 8

One change of mechanism, found by reading this plan against `docs/versioning-requirements-and-plan.md`,
and two errors it exposed that were already in draft 7.

**Record changes are undone through versions, not journal payloads.** Draft 7 kept every inverse in the
change journal. That made the journal the only copy of a deleted record, let a payload cap decide
whether a wide record's delete could be undone, and answered "touched since" with a content hash.
Every collection the assistant writes to is now versioned (**SC-12a**), and a record change's
`DataChange` names the version it replaced and the one it produced (**TR-2b**). Undo restores versions
through six contract operations that both backends implement (**SC-12**), and the before-and-after
table is a diff of two versions (**TR-6**). What remains of the journal's payload rules applies to
structural changes, where the journal is still the only copy (**AG-11e**, still-open item 10).

**Record inserts and updates were never unconditionally reversible.** Draft 7's AG-11c said they were,
and the reason it gave — putting old values back cannot violate a rule the new ones satisfy — is false.
It overlooks every other record:
- A's email goes x→y, then B takes x, and undoing A would put x back beside B.
- A's conference goes c1→c2, then c1 is deleted, and undoing A would point it at nothing.
- Undoing an import deletes a conference that a later expense references, and restrict refuses.

**AG-11c** now classifies a record change by its collection — `Reversible` with no unique column and no
relation, `ReversibleWithConditions` otherwise — and **D-17** lists the cases.

**Undo cannot check each write as it replays, and cannot merge a record's writes either.** A restore
creates a new version, so checking a record's earlier change against the head fails once its later
change has been undone. Merging a record's changes into one restore fails when they interleave with
another record's changes on a unique column — X a→b, Y c→a, X b→c. **AG-11a** checks each record once,
before anything is replayed, and **AG-11f** replays every change in exact reverse order.

**Deletion against durable undo, restated.** A deleted record is restorable from version history for the
compensation window. Ending the window early works per request and per thing, and the engine clears
released bytes, so draft 7's "only after compaction" qualification is gone (**NF-4d**, **NF-4d1**). The
erasure boundary is stated: diagnostics payloads inside, the user's own conversations outside, and the
card says so.

**Scheduling.** Step 3.2 was already done, and carries a note instead of a new prompt. Step 4.7 now waits
for the versioning plan's Phases 1 to 7 and step 9.3, and stops rather than building undo on payloads
(**R-9**). Step 9.2 has three retention numbers instead of two, and step 9.4 no longer compacts.

---

## Appendix: what changed in draft 9

Draft 8 took its undo rules from the versioning plan's draft 2. Three later drafts of that plan changed
what the rules have to say, and draft 9 brings this plan in line with them.

**Every blocker is found before anything is written.** Draft 8 checked only that each record was still at
the version the request produced. A unique collision, a reference to a record that has gone, or a
deleted record still referred to from outside was found only during the replay — at the first one it met,
after the unit of work had started writing. **AG-11a** now validates the end state as well, computing it
from each record's current values and `DiffVersions`, and names every blocker before any write. **D-17**,
**N-11** and step 4.7 follow.

**The replay's limit is stated.** Validation checks the state an undo ends in, but the replay passes
through intermediate states. A record that went a→b→c inside the request, whose b another record has taken
since, can therefore still refuse at replay. **AG-11a** says so, makes it a test, and points at the engine
change that would remove it (versioning plan, F-11).

**A partial undo is validated before it is offered.** Leaving a blocked record out changes the state the
rest of the undo ends in, and can create a collision the whole undo did not have. **AG-11b** validates the
rest as an undo of its own, and names such a collision rather than offering a partial undo that would fail.

**Purge and erase now match the engine's guarantees.** The versioning plan's drafts 3 to 5 made a purge
remove the values of the versions it removes, and made erasing and purging transactions discard their
journal frame at commit. **NF-4d1** and step 9.4 now test right after the commit, with no close, and check
that a per-request purge leaves nothing of the purged versions.

---

## Appendix: what changed in draft 10

The versioning plan landed — Phases 1 to 9, on this branch — and six proposed refinements were read
against both plans. Two were open items of §10 that the engine's arrival made settleable; four were the
future items of that plan's register, and they stay future, for the reasons below.

**The plan said "still to build" about things that are built.** §3.2, R-9, Phase 3, Phase 4 and step 4.7
described the versioning plan as pending. They now record what its Phase 9 built for the assistant — the
journal's version references, SC-12's six operations on both backends, the compensation as a test-local
helper, erasure through the assistant — and steps 4.7, 9.2 and 9.4 adopt it. BR-3 and BR-3b no longer
say versioning is deferred; what paging still lacks is a query as of a moment, that plan's F-3.

**A removed field disappeared from the past.** The engine reports every value the current schema cannot
show (versioning plan, V-10, G-6), and the built `DiffVersions` discards that report, so a change to a
field since removed vanishes from the before-and-after table, and a lossy retype's old value with it —
the one place the assistant made the engine's history silently lossy. **TR-6a** requires a trace to be
shown as it was, with a note of what became of a field since; **SC-12** requires `DiffVersions` to carry
what the shape cannot show; §10's item 4 is settled by them; step 7.3 closes the gap.

**A partial undo is its own question.** AG-11b offered the rest as a separate action and left open whether
taking it needs a confirmation. It does: "undo these 499 and leave that one" is not the question that was
refused. **AG-11b** makes taking the offer a card of its own, in UI-4's shape and answered from
`WaitingForUser`; N-11, steps 4.5 and 4.7 follow; §10's item 9 is settled.

**Four future items of the versioning plan stay future.** A version view in the browser (F-10) is the one
that section 1.3 argues for, and the engine now has everything it needs, so the decision is recorded as
§10's item 14 with what it would cost this plan, to be settled before Phase 6. Undoing a structural change
from history (F-7) still lacks the engine's re-add of a field from history, so the journal's cap still
decides reversibility (item 10, now saying what the engine already keeps). Deferred constraint checks
(F-11) would remove AG-11a's stated replay limit, which that plan's R-14 rates rare and whose refusal
names the record; nothing here waits for it. Incoming relations in a related restore (F-2) never reach
this plan: the assistant restores by replaying its own change records in reverse (AG-11f, SC-8a), and
never asks the engine to find a relation's holder through history.
