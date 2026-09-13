# Entity query — requirements and development plan

**Status.** Draft 3, written 2026-09-13 against the working tree of that date.

**What this is.** A new query mechanism on `DbEntities<T>.Query`, built on the parts of the
engine that already work, adding three things they do not cover: an **order**, **`Skip` and
`Take`**, and **filtering across a declared `Relation`**.

**What this is not.** It is not a join. Nothing here returns records of two collections, adds a
column from another collection to a result, or aggregates across one. A relation is used to
*narrow* the records of the collection being queried, and the result is still records of that
collection and nothing else.

---

## 1. Scope

### 1.1 In scope

| | |
|---|---|
| Ordering | One or more columns, ascending or descending, with record identity as the final tiebreaker |
| Paging | `Skip` and `Take` over a defined order, with `Take` alone allowed as a cap |
| Relations | Filtering the queried collection by a predicate on a related collection, one hop, `Any` and `None` |
| Entry point | A fluent builder on `DbEntities<T>`, producing an immutable request |
| Diagnostics | The existing `QueryReport`, extended, and a nested report per relation step |

### 1.2 Out of scope

Projections and returning anything but whole records; joins that widen a row; aggregation;
grouping; more than one hop across relations; the `All` quantifier; intersecting two indexes for
one query; a cost model based on statistics.

---

## 2. Decisions

**Q-1 — The builder is the entry point; `NormalizedQuery` stays exactly what it is.** A fluent
builder on `DbEntities<T>` collects the predicate, the order, the paging and the relation steps,
and produces one immutable request that the planner reads.

*Why:* `NormalizedQuery` is a document-layer type that means one precise thing — a conjunction of
column comparisons plus a residual. Ordering, paging and relations are engine-layer concerns that
need the index catalogue and the relation catalogue, neither of which the document layer can see.
Adding them to `NormalizedQuery` would put a type that knows nothing about storage in charge of
decisions only storage can make.

*Consequence:* the existing `Query(NormalizedQuery, IReadOnlyList<Ulid>)` stays, unchanged and
working, as the narrow entry the builder itself uses.

**Q-2 — An order is part of the query, and `Skip` requires one.** A query with `Skip` and no
order is refused at build time with a typed error.

*Why:* the executor walks whatever the access path yields, and `IndexSeekPath` says so in its own
comment: the path promises the right records, not an order. Page two of an unordered query is not
the records that follow page one; it is a second arbitrary handful. Paging over that is not a
weaker guarantee, it is a wrong answer that looks like a right one.

*Consequence:* `Take` without an order remains legal and means *any N*, which is what a safety cap
is for. `Skip` without an order does not compile into a plan.

**Q-3 — Ascending over an index is free; descending is a sort, until the leaf chain runs both
ways.** `BPlusTree.Range` descends once and then follows `NextPageIndex` from leaf to leaf. There
is no previous pointer, so there is no backward walk to have.

*Why:* stated here rather than discovered in a benchmark. A descending order over an indexed
column looks as though it should be as cheap as an ascending one and is not, and the difference is
a page-format change rather than a query change.

*Consequence:* descending is supported, by sorting. The report says which happened, so the cost is
visible rather than inferred. Adding a backward chain is recorded as R-1 and is not in this plan.

**Q-4 — Record identity is the final tiebreaker of every order, always.** The declared columns
first, then `Ulid`.

*Why:* ten thousand records sharing a date are ten thousand records in the same place, and a page
boundary among them repeats some and skips others. The engine's own secondary keys are already the
composite of value and identity (D-3), so the order this produces is the order the index is in,
which is what makes the free case free.

*Consequence:* every order is total and repeatable, and a later seek-after cursor has something
well defined to seek after.

**Q-5 — A relation filter is a semi-join, rewritten into an `In` conjunct.** The inner predicate
runs against the far collection through the same planner, the matching values of the joined column
are collected and deduplicated, and the step becomes `nearColumn In (those values)` on the query
that was asked.

*Why:* it turns a capability the engine does not have into one it already has. An `In` over an
indexed column is the access path the planner prefers first, so a relation filter costs one inner
query plus the seek it was going to do anyway, and no new execution machinery exists to go wrong.

*Consequence:* the key set is held in memory, so it is bounded by Q-9 and the bound fails loudly
rather than degrading quietly.

**Q-6 — One hop, `Any` and `None`. `All` is declared and refused.** A relation step inside a
relation step is refused at build time.

*Why:* `Any` and `None` are the two questions people ask of a relation, and both have an exact
answer here. `All` quantifies over the far records reached from one near record, which is only
meaningful when the near column holds more than one value, and it does not.

*Consequence:* `All` throws a `NotSupportedException` naming the reason, the way
`RetentionPolicy.KeepVersions` already does. A half-implemented quantifier in the type is worse
than a refused one.

**Q-7 — `Take` stops the walk; `Skip` never materialises what it skips.** Both are properties of
the execution and not filters over a finished list.

*Why:* `QueryExecutor.Execute` builds a `List<QueryMatch>` of every match, and a `Take` applied to
that list would read every page and deserialize every matching document to return twenty. The
whole point of the existing executor is that only a surviving record becomes a document; paging
has to preserve that rather than undo it.

*Consequence:* the executor becomes lazy in its walk. `DocumentsMaterialised` for a page of twenty
is twenty, whatever the offset, and that is an assertion rather than an intention. The laziness
stays **inside** the pipeline: `Run` materialises the page and hands back a finished report (DG-5),
because a report that completes when the caller finishes enumerating is untrue at the moment the
diagnostics event carries it.

**Q-8 — `None` narrows nothing, and the plan says so.** `NotIn` joins the operator set as a
comparison the planner will never seek by.

*Why:* an anti-semi-join has no index shape. The records not carrying one of ten thousand values
are not a stretch of any tree. Pretending otherwise would produce a plan that claims an index and
performs a scan.

*Consequence:* a query whose only condition is a `None` relation step reads the collection, and
the access path reports a full scan with that as the reason. A `None` combined with an indexable
condition uses the index for the other condition and re-checks the set per record. `NotIn` means
**set membership** and nothing else: no three-valued logic, and a record whose column is null
satisfies it, because its key is in no set (RL-5, RL-10).

**Q-9 — The semi-join key set is capped in bytes, and the cap fails the query.** A configured
maximum size of encoded keys, with a typed error naming the relation, the size reached and the cap.

*Why:* the alternative is an out-of-memory failure at an unpredictable size. Bytes rather than a
count of values, because a `Ulid` key and a 256-byte text key are not the same memory and a count
would cap them as though they were.

*Consequence, once Q-13 exists:* the cap guards **memory**, not the choice of strategy. A large key
set is a reason to change strategy, not to refuse; the cap is what stops the set itself from being
the problem.

**Q-10 — A relation query reports two plans, not one.** The report of the outer query carries the
report of each inner query it ran.

*Why:* this repository has already been bitten once by an aggregate hiding a cost: a single
logical call that was twenty-five round trips looked like one call in the total. A relation filter
is two queries, and a report that sums them into one page count cannot show that the inner query
scanned.

**Q-11 — A request, a plan and an execution are three things, not one.** The **request** is
immutable and validated against nothing but itself. The **plan** is produced against a catalogue
snapshot and carries the catalogue version it was planned against. The **execution** re-checks that
version before it reads anything.

*Why:* the first draft of this document asserted three things that cannot all hold. Validation
happened when the request was built, `Explain`'s plan was required to equal the plan `Run`
executes, and an index was allowed to disappear between them. Whichever two you keep, the third
breaks. Splitting the stages keeps all three true of the thing each one is about.

*Consequence:* a column name, a relation name and **whether a relation is self-referential** are
validated at **plan** time, because that is the first moment a catalogue exists to check them
against. Only the refusals that need no catalogue stay at build time.

*And a version check on its own is not enough.* Comparing a version and then executing is a race:
the catalogue can change between the comparison and the read. Planning and execution therefore hold
a **catalogue read lease**, and the version is what a plan handed to a caller is checked against
when it comes back. Inside one `Run` the lease covers both stages and no staleness can arise; a plan
obtained from `Explain` and executed later is the case the version exists for (QM-2b).

**Q-12 — A page is served by a bounded heap, not by sorting everything.** Where an order needs a
sort, the ordering stage keeps at most `Skip + Take` records by the order and discards the rest as
it goes.

*Why:* a page of twenty does not need a hundred thousand records in memory, and a heap of
twenty-plus-offset gives exactly the same answer.

*What it does not do, stated because an earlier draft claimed otherwise:* it does **not** make a
sorted plan streaming. Every candidate still has to be inspected, because an unseen record may
outrank a retained one, so enumeration cannot stop early. The heap bounds memory and document
materialisation and nothing else, and the report says the plan was not streaming (DG-1).

*Consequence:* memory is a function of the page and not of the match count, except for a deep
offset or a requested total, which are the two cases that still hold everything. Those are where
the sort cap of OR-7 applies.

**Q-13 — An `In` is many probes, and the plan may prefer one pass instead.** A seek over N values
is N descents of the tree, and the engine has no page cache.

*Why:* the first draft claimed a relation filter costs one inner query plus one seek. It does not.
Ten thousand keys are ten thousand random descents multiplied by the tree height.

*Consequence:* two strategies. Below a crossover, seek the values. Above it, walk the outer
collection once and test membership against a hash set of the keys, which is sequential rather
than random. The report says which ran and how many probes it made, so the crossover can be
measured rather than assumed.

**Q-14 — An order may choose the access path, and the planner must be told so.** Today the path is
chosen by the predicate alone. An ordered query has a second claim on that choice, and the two can
want different indexes.

*Why this is a correction and not an addition:* the first draft's own opening scenario asked for
`Where(city = 'Lviv').OrderBy(date)` with an index on `date`, and claimed the order came free from
the walk. It cannot. The planner picks one path, the only conjunct names `city`, and nothing would
ever make it walk the `date` index — a range path needs an ordered predicate on that column and
there is none. The scenario was unachievable as written.

*Consequence:* an **ordered index walk** becomes a path the planner may choose *for the order*,
with the predicate applied as a filter over what it yields. It needs no new executor: an index range
with both bounds open already walks a whole index in key order. What it needs is a rule for
choosing, and the honest rule is stated in OR-2a rather than assumed.

*And the limit stated with it:* serving a selective predicate on one column and an order on another
from one walk needs a composite key over both. The engine's secondary indexes are single-column, so
one of the two is always paid for. A query can be filtered cheaply and then ordered, or walked in
order and then filtered, and the planner chooses which.

---

## 3. What already exists, and is used rather than rebuilt

These are capabilities of the engine as it stands. The new mechanism composes them; none of them
is reimplemented.

| Capability | What it gives this plan |
|---|---|
| **`NormalizedQuery`** | A predicate already split into conjuncts a planner can match against an index and a residual it cannot. The split is exact, so narrowing by the conjuncts and re-checking the rest returns precisely the right records |
| **`QueryNormalizer`** | Turns an expression tree into that form, lifting only the operands of a top-level `And`, which is the only place a conjunct is required on its own |
| **`QueryPredicate.IsIndexable`** | Whether a conjunct could be answered by an index at all, without consulting one |
| **`QueryPlanner`** | Access-path selection as a written rule: identity, then equality seek, then range, then scan. Predictable, and therefore testable against an expectation |
| **`AccessPath.IsExact`** | Whether the path's records still need the conjunct re-checked. Folded and truncated keys match more than the value they were made from, and this is what keeps that honest |
| **`QueryExecutor`** | Predicates evaluated against the record as it lies on the page, through the same expression tree the query arrived as, with only surviving records materialised |
| **`DataPageManager.ReadFields`** | One field of a record without deserializing the document, including through the lazy schema migration |
| **`QueryReport`** | Pages read, records examined, records matched, documents materialised, elapsed, and the path that was chosen — carried with the result rather than logged beside it |
| **`QueryService.QueryExecuted`** | One place every query of the engine passes, which is what makes the reporting complete rather than remembered per call site |
| **`BPlusTree.Range`** | One descent to the lower bound, then a walk of linked leaves: the sequential read an ordered walk needs |
| **`KeyEncoder`** | Order-preserving encoding for every scalar, with a type tag, so the index order *is* the value order |
| **Composite index key** | Value plus record identity, so entries for one value sit together and ties have a defined position |
| **`RelationCatalog`** | The declared relations, and the guarantee that the target column of each is indexed — creating a relation creates the index it needs |
| **`RelationStepExpression`** | The shape of a relation step in a predicate, with its quantifier, already parsed and already routed into the residual |
| **`PrimaryKeyPath`** | Lookup by identity, which is what a reverse-direction relation filter collapses to when the far column is the identity |

Two of these are load-bearing in a way worth stating. **Relations already guarantee their target
index**, so the inner query of a semi-join has an index to use in the common direction without
this plan creating one. And **`RelationStepExpression` already exists and already throws**: a query
carrying one normalises cleanly today and then fails at execution. This plan is what makes that
type mean something.

---

## 4. Requirements

### 4.1 The query model and its builder (group QM)

**QM-1 (M).** `DbEntities<T>` shall expose a fluent builder that collects a predicate, an order,
`Skip`, `Take` and relation steps, and produces one **immutable request**. Immutable means the
nested collections too: the order and the relation steps are held as `ImmutableArray<T>` or as
private arrays **copied on construction**, never as a read-only view over a list something else
still holds.
*AC:* the request type exposes no mutable member, asserted by reflection; passing a mutable
collection into the builder and then changing it leaves the built request unaltered.

**QM-1a (M).** The **builder itself shall be a persistent immutable value**, and every method shall
return a new one.
*AC:* a base builder extended two different ways produces two requests, neither carrying the
other's additions. This is a property of the design rather than of care: a mutating builder could
not satisfy it however carefully it were used, and because the builder is immutable there is no
"mutate it after building" case left to test — the test that replaces it mutates the **inputs**.

**QM-2 (M).** The builder shall be runnable and explainable: `Run` returns the records and the
report, and `Explain` returns a **concrete immutable plan** without touching a page beyond the
catalogue. That plan is a value a caller may hand back to `Run`, which executes it rather than
planning again.
*AC:* `Explain` on a query over a million records reads no data page; against the same catalogue
version, the plan `Explain` returns is the plan `Run` executes, asserted by executing the returned
plan directly.

**QM-2a (M).** A plan shall carry the **catalogue version** it was planned against, and a plan
handed back to `Run` shall be **refused when that version has moved**, rather than re-planned.
*AC:* a plan executed after an index was created or dropped fails with a typed error naming the
version it expected and the version it found; a plan executed against its own version runs.

*Why refusal and not a silent re-plan:* a caller who held a plan measured a cost. Re-planning
returns the right records at a cost they did not ask for and cannot see, which is the failure mode
this whole section exists to avoid. Refusing is noisier and honest, and re-planning is one line away
for a caller who wants it.

**QM-2b (M).** Planning and execution shall hold a **catalogue read lease**, so that the catalogue
cannot change between the check and the read.
*AC:* a schema change attempted during a query waits for the lease rather than interleaving with it;
a test drives a create and a drop concurrently with a long walk and asserts the query neither fails
nor reads a half-changed catalogue.

*Why a version comparison alone was not enough:* checking a version and then reading is a race, and
the window is the whole query rather than an instant. Inside one `Run` the lease covers planning and
execution together, so no staleness can arise at all; QM-2a's version is for the other case, a plan
that left the engine through `Explain` and came back later.

**QM-3 (M).** The existing `Query(NormalizedQuery, ids)` shall keep working unchanged. It shall
**not** be the path the builder takes: both shall sit on one shared lower-level candidate pipeline
(DG-4), because the existing entry point materialises every match into a list and can express
neither an order nor a page nor a choice of `In` strategy.
*AC:* every existing query test passes untouched; a test asserts both entry points produce the same
records for a query both can express, and the builder's paging and ordering are shown not to be
implemented by filtering the old method's result.

*Why the compatibility goal was right and the mechanism was not:* an earlier draft said the builder
would take the existing path. Keeping the old signature working is a real obligation; routing new
semantics through an API that materialises everything would have undone the point of the new ones.

**QM-4 (M).** Refusals that need **no catalogue** shall be typed errors raised when the request is
built: `Skip` without an order; a relation step inside a relation step; the `All` quantifier; a
negative `Skip` or `Take`; a relation step in a position other than an AND-conjunctive one (RL-3).
*AC:* each is a test, each message names what was wrong, and none of them reaches a planner.

**QM-4a (M).** Refusals that need a **catalogue** shall be typed errors raised when the request is
planned, which is the first moment there is a catalogue to check against: an order naming a column
the collection does not have; an unknown relation; a relation step whose direction does not match
the collection being queried; and **a self-relation with no stated direction**.
*AC:* each is a test; the message lists what is declared, so an unknown relation names the ones
that exist; and `Explain` raises them exactly as `Run` does, because both plan.

*Why the self-relation refusal moved:* a step names a relation, and whether that relation's two
sides are the same collection is a fact about the catalogue. A builder holding only a name cannot
know it. It would be a build-time refusal only if the builder took a resolved relation rather than
a name, which would make every query depend on a catalogue read before it was written.

**QM-4b (M).** `Skip`, `Take` and their sum shall be held and compared in **64-bit arithmetic**,
saturating rather than wrapping, and an offset past the end of the result shall be an empty page
rather than an error. **No collection shall be allocated at a capacity taken from `Skip + Take`.**
*AC:* `Skip(int.MaxValue).Take(int.MaxValue)` builds, plans and returns nothing, without wrapping to
a negative window and without attempting an allocation of that size; the ordering stage grows as
records arrive rather than reserving the window in advance.

*Why the allocation clause is not fussiness:* `Skip + Take` comes from the caller. A heap sized from
it up front turns a one-line query into an out-of-memory failure, which is a denial of service
wearing the clothes of a page request.

**QM-5 (S).** `Take(0)` shall be a legal query that returns nothing and still reports.
*AC:* it reads no data page and returns an empty result with a report, rather than being treated
as "no limit".

### 4.2 Ordering (group OR)

**OR-1 (M).** An order shall be a sequence of **(column, direction)** pairs, applied in sequence,
with **record identity appended as the final tiebreaker, in the direction of the last declared
column**. The comparator is one object, shared by the sort and by anything later that has to
compare two positions in this order.
*AC:* two records equal on every declared column come back in the same relative position on every
run; under a descending last column their identity order is descending too, which is the order the
index key is already in.

*Why the tiebreaker is not simply always ascending:* the free case of OR-2 takes its order from an
index walk, and the index key is the value and the identity together in one direction. A tiebreaker
that ran the other way would make the walk and the sort disagree about the same query.

**OR-2 (M).** Where the **requested order is a prefix of the index key order**, directions
included and the identity tiebreaker of OR-1 counted as part of the request, the order shall be
taken from the walk and the ordering stage shall retain nothing.
*AC:* an ordered query whose order the index key satisfies reports its order as coming from the
index and retains nothing; a query asking for one column more than the key resolves reports an
ordering stage instead.

*The direction of this rule matters, and an earlier draft had it backwards.* A walk yields records
in the index key's order, so it satisfies a request only when the key **refines** the request: the
request must be a prefix of the key, not the other way about. With a key of `(date, identity)`, a
request for `(date, identity)` is satisfied. A request for `(date, cost, identity)` is **not**,
because records sharing a date come off the walk in identity order and the request wants them in
cost order. Stated the wrong way round, that second case reads as satisfied, and the query returns
the wrong sequence while reporting that nothing needed ordering.

*What it reduces to today:* the engine's secondary indexes are single-column and their key is that
column plus the record identity, so the rule currently admits exactly one shape — a single order
column matching the indexed one, in the key's direction. Writing the rule rather than the shape
costs nothing and is already right if composite indexes are ever added.

**OR-2a (M).** The planner shall be able to choose an **ordered index walk for the order's sake**,
applying the predicate as a filter over what it yields, and shall choose between that and the
predicate's own path by a **stated rule**: prefer the predicate's path and order afterwards, unless
the query is bounded by `Skip + Take` and the predicate's path is **not** selective, in which case
walking in order and filtering can return the page without examining the whole collection.
*AC:* `Where(indexed equality).OrderBy(other indexed column).Take(20)` produces a plan that names
which of the two it chose and why; both choices return the same records in the same order, asserted
by running the query twice with the rule forced each way.

*Why the rule is a rule and not a cost model:* choosing well needs selectivity, and nothing collects
statistics. A written rule is at least predictable and therefore testable, which is the same
argument the existing planner already makes for itself. Where the rule is wrong it is wrong in the
diagnostics line rather than in a timing nobody attributes.

**OR-3 (M).** Every other order shall be produced by an ordering stage over **keys and addresses,
never over documents**, holding the order columns and the record address and nothing else.
*AC:* ordering 100 000 matching records by an unindexed column and taking 20 materialises 20
documents, and the stage's memory is asserted not to grow with the width of the record.

**OR-3a (M).** Where the page is bounded, the ordering stage shall keep at most **`Skip + Take`**
records by the order and discard the rest as it goes (Q-12). It is **memory-bounded and not
streaming**: every candidate is still examined, because an unseen record may outrank a retained one.
*AC:* `OrderBy(unindexed).Take(20)` over 100 000 matching records holds 20 records in the ordering
stage, not 100 000, asserted on the stage's high-water mark; the same query reports examining all
100 000 and reports the plan as not streaming.

**OR-3b (M).** Only a **deep offset or a requested total** shall hold every matching record, and
those are the cases OR-7's cap governs.
*AC:* the report says which of the two the ordering stage did, bounded or complete, and a test
covers each.

**OR-4 (M).** Descending shall be supported and shall be a sort, because the leaf chain runs one
way only (Q-3).
*AC:* a descending order over an indexed column returns the right records in the right order and
reports that it sorted; a test pins the reason so that the day a backward walk exists, the test
that must change is the one that recorded the limitation.

**OR-5 (M).** The order shall be applied **after** the conjuncts and the residual, and never
before.
*AC:* a query that filters to 5 records out of a million sorts 5 records, not a million.

**OR-6 (M).** The **cross-type order shall be part of the contract**, not an internal detail. It is
the tag order of the key encoding, and the document states it: null, boolean, signed integer,
unsigned integer, floating point, decimal, date and time, time span, guid, ulid, text. Values group
by type in that order and the report says the column was mixed.
*AC:* a column holding numbers and text returns all of one then all of the other, deterministically,
and says so; every supported pair of types has a test.

*Two edges that will surprise someone, so they are pinned rather than left to be met:* **signed and
unsigned integers do not share a tag**, so an `Int` value and a `UInt` value sort in separate runs
however close their numbers are. And **numerically equal values of different types are not equal
keys**, so `5` as an integer and `5.0` as a floating-point value order apart rather than together.

**OR-6a (M).** The comparator shall be defined in terms of the **encoded key order** and shall never
fall back on a CLR default comparison, in the ordering stage or anywhere else.
*AC:* a test asserts the ordering stage and an index walk agree on the same data for every supported
type; the same-type edges the key encoder already handles have tests of their own — negative zero
against zero, `NaN`, decimals equal at different scales, and `Guid`, whose CLR comparison is not the
order of its bytes.

*Why this is a requirement and not an implementation note:* an in-memory sort that reached for the
CLR's comparison would agree with the index for most values and disagree for exactly those four,
which is the kind of difference that surfaces as one wrong page long after it was written.

**OR-7 (M).** The ordering stage shall have a **memory cap**, and exceeding it shall fail with a
typed error naming the stage, the size reached and the cap.
*AC:* a complete sort over more records than the cap allows fails before a page is returned, with
all three numbers in the message.

*Why this exists and the earlier draft only had a risk entry:* the semi-join already had a cap
requirement while the sort had only a note. Two unbounded in-memory structures, one guarded and one
not, is an inconsistency rather than a judgement.

**OR-8 (M).** **Null ordering shall be defined**: a null sorts before every value ascending and
after every value descending.
*AC:* a column with nulls orders deterministically in both directions, and the same query answered
through an index walk and through a sort puts the nulls in the same place.

*Why this direction and not a choice:* the key encoding already sorts the null tag before every
value, so this is what an index walk does. A nulls-last option would mean the walk could no longer
satisfy the order, which would cost OR-2's free case to buy a preference.

### 4.3 Paging (group PG)

**PG-1 (M).** `Take(n)` shall **stop the walk** once `n` records have been matched, and shall not
read further pages.
*AC:* `Take(20)` over a collection of 100 000 with no predicate reads the pages holding the first
20 records and no more, asserted on `PagesRead`.

**PG-2 (M).** `Skip(n)` shall **not materialise what it skips**. A skipped record costs the fields
the predicate and the order name, and no document.
*AC:* `Skip(1000).Take(20)` reports `DocumentsMaterialised` of 20.

**PG-3 (M).** The cost of `Skip` shall be **stated rather than implied**: it grows with the offset,
because the records before the page are still examined. The report carries how many were skipped.
*AC:* `Skip(10000).Take(20)` reports 10 000 skipped, and the documentation of the builder says
that a deep offset costs what it costs and points at R-2.

**PG-3a (M).** The cost of a page shall be stated **per combination of access path and order
source**, because the same `Skip` is three different costs. This table is the specification the
tests check.

| Access path | Order source | Entries or records examined for `Skip(s).Take(n)` | Documents materialised |
|---|---|---|---|
| Index range or seek chosen by the predicate | index walk over that same index (OR-2) | `s + n` matching records | `n` |
| Ordered index walk chosen for the order (OR-2a) | that walk | index entries until `s + n` **match the predicate**, which is the whole index when the predicate is unselective | `n` |
| Index range or seek | bounded heap (OR-3a) | every matching record | `n` |
| Full scan | index walk | not reachable: a scan has no key order | |
| Full scan | bounded heap (OR-3a) | every live record | `n` |
| Any | complete sort (OR-3b) | every matching record, all held | `n` |

*AC:* each reachable row is a test asserting what was examined and what was materialised, and the
unreachable row is asserted to be unreachable rather than left to be assumed.

*The second row is the one that misleads.* An ordered walk is bounded by the page only when the
predicate matches most of what it passes. Filtering one city out of forty from a date-ordered walk
reads about forty entries for every record it keeps, so a page of twenty costs eight hundred
entries, and a city with no records at all costs the entire index. That is the price of not having a
composite key over both columns, and it is why OR-2a's rule prefers the predicate's own path unless
the query is bounded and that path is unselective.

**PG-4 (M).** `Take` without an order shall be legal and shall mean **any n**; `Skip` without an
order shall be refused (QM-4). What is guaranteed is stated positively: every returned record
satisfies the predicate, and the count is the lesser of `n` and the number of matches. **Which**
records is unspecified.
*AC:* `Take(5)` with no order returns five records that all satisfy the predicate, and a report;
`Skip(5)` with no order does not build.

*Why the guarantee is worded that way:* nondeterminism cannot be asserted by a test — two runs
agreeing proves nothing and two runs differing proves only that this build differs. The invariant
that holds whatever the walk does is the thing to assert.

**PG-5 (M).** Where an ordering stage is needed (OR-3), paging shall be **part of it rather than
after it**: the stage is bounded by `Skip + Take` (OR-3a), so the page falls out of the stage and
is not filtered from a finished list.
*AC:* the report distinguishes records examined, records matched, records retained by the ordering
stage and records returned; for a bounded stage the retained count is `Skip + Take` and not the
matched count, and for a complete sort it is the matched count.

**PG-6 (S).** A **total count** shall be available only when asked for, and shall be reported as a
separate figure with its own cost.
*AC:* a paged query that did not ask for a total does not compute one; one that did reports it and
the report shows the extra pass.

### 4.4 Filtering across a relation (group RL)

**RL-1 (M).** A relation step shall name a **declared relation** and shall be resolved against the
`RelationCatalog`, in whichever direction the queried collection sits.
*AC:* querying the source collection steps to the target and querying the target collection steps
to the source, both from the same declared relation; an unknown relation is refused at plan time
(QM-4a).

**RL-2 (M).** Direction shall be named by **role and not by a flag**: a step goes either *to the
target* or *to the source* of the named relation. It is inferred where the queried collection
appears on exactly one side, and **required** where the relation's two sides are the same
collection.
*AC:* a self-relation with no stated direction is refused at **plan** time with a message naming
the collection (QM-4a), because a builder holding only a relation's name cannot know its two sides
are the same collection; both directions of a self-relation resolve when stated, and each is a
worked example in the builder's documentation — a task whose parent task matches, and a task whose
subtasks match.

**RL-3 (M).** A relation step shall be accepted **only in an AND-conjunctive position**, and a step
under an `Or` or a `Not` shall be refused at build time naming the relation and the position.
*AC:* `Where(a.Or(relationStep))` is refused; `Where(a.And(relationStep))` is accepted; nested
`And`s are still conjunctive and are accepted.

*Why this is a correctness rule and not a limitation of convenience:* the rewrite lifts the step out
of the predicate and re-adds it as a conjunct. That is only equivalent where the step was **required
on its own**, which is exactly the top-level conjunction the normaliser already lifts from. Under an
`Or` the step constrains nothing by itself, so removing it changes what the query means, and an
implementation that did it anyway would return records the caller did not ask for while reporting
success. Supporting those positions needs an AST rewrite that proves equivalence, which is not in
this plan.

**RL-3a (M).** `Any` shall be executed as a **semi-join**, and the rewrite shall be stated as a
function on the normalised query: the step is **removed** and an `In` conjunct on the near column is
**added to the conjuncts**, carrying the projected far values, deduplicated.
*AC:* deduplication is asserted, because a repeated value would seek twice and return the same
record twice; and **no plan reaching the executor contains a `RelationStepExpression`**, asserted by
a check that walks the conjuncts and the whole residual tree recursively, as a planner invariant
rather than as a test of one case.

*Why that invariant is not pedantry:* `RelationStepExpression.Execute` throws by design. A rewrite
that adds the conjunct and leaves the step behind is not a query evaluated twice, it is a query that
crashes.

**RL-3b (M).** The `In` shall be executed by one of **two strategies**, and the report shall say
which (Q-13). Below a crossover, seek each value. Above it, walk the outer collection once and test
membership against a set. The crossover compares the **estimated probes**, which is the distinct key
count multiplied by the index height, against the **pages of the outer collection**, both of which
the catalogue already knows.
*AC:* a small key set reports seeks and their number; a large one reports one pass and no seeks; the
crossover is a configured value with a measured default, exposed in the diagnostics; and a test
asserts both strategies return the same records for the same query.

**RL-3c (M).** The **membership pass shall be its own access path**, not an index path wearing an
index path's description.
*AC:* the plan names it distinctly, and a reader cannot mistake a sequential membership pass for a
seek; `In` appears explicitly among the shapes the planner declares it can answer.

**RL-4 (M).** The inner query shall not be a request at all: it shall be a **key projection**
carrying exactly four things — a predicate, the projected column, the catalogue lease it runs under,
and a cancellation token — with **no order, no paging and no materialisation**, so that the waste
cannot be expressed rather than merely discouraged.
*AC:* an inner query matching 5 000 records reports `DocumentsMaterialised` of 0; a reflection test
asserts the projection type has those four members and no member for an order, a page or a result.

**RL-5 (M).** `None` shall be executed as an **anti-semi-join** through a new `NotIn` comparison
that the planner shall never choose an access path by (Q-8). `NotIn` is defined as **set
membership**: the record's key is not in this set. It does **not** inherit three-valued logic.
*AC:* a query whose only condition is a `None` step reports a full scan, with the reason naming
the anti-join rather than "no index"; the same query with a second, indexable condition takes the
index for that condition and re-checks the set per record; a record whose near column is null
**satisfies** `NotIn`, because its key is in no set, and a test pins that, because the intuition
people arrive with is the SQL one.

**RL-6 (M).** The key set shall be held in a **purpose-built set of compact encoded keys** and
capped by the **bytes that set has actually allocated**, not by a count of values and not by the
encoded length alone. Exceeding the cap fails with a typed error naming the relation, the size
reached and the cap (Q-9).
*AC:* an inner query whose set exceeds the cap fails before any outer page is read, and the message
contains all three; a test asserts the figure the cap is compared against is the set's own
allocation, not the sum of its keys.

*Why the sum of encoded keys is not a memory cap:* a hash set of byte arrays costs buckets, object
headers and references per entry, and on short keys that overhead is larger than the keys. Capping
the payload and calling it memory would under-count by a multiple, which is worse than capping a
count, because it looks rigorous.

*What the cap is for, now that RL-3a exists:* it guards **memory**, not the choice of strategy. A
large key set is no longer a reason to fail, because above the crossover the query switches to one
pass and a hash set. The cap is what stops the set itself from being the problem.

**RL-7 (M).** `All` shall be refused with a `NotSupportedException` that says why.
*AC:* the message names the quantifier and states that a single-valued near column has nothing for
it to quantify over.

**RL-8 (M).** Exactly **one hop** shall be supported. A relation step whose inner predicate
contains another relation step is refused at build time.
*AC:* the refusal names both relations.

**RL-9 (S).** The rewritten `In` shall go to the planner **like any other conjunct**, and the
access path shall be whatever the planner's existing rule chooses for it.
*AC:* filtering conferences by a predicate on their expenses reaches the conferences through the
primary index, and the report says so; filtering by a unique indexed column reaches them through
that index; neither is a special case in the relation code.

*Why this is a weaker requirement than the one it replaces, and better:* the planner already
prefers identity over a unique index and a unique index over an ordinary one. A rule naming
`PrimaryKeyPath` would restate that in a second place, where it could drift. The requirement now
describes the outcome and leaves the choosing where it already lives.

**RL-10 (M).** Null keys shall be **excluded from the set** rather than encoded into it: a far
record whose join column is null contributes nothing, and a near record whose join column is null
is a member of nothing.
*AC:* `Any` is false for a near record with a null join value and `None` is true for it; a far
collection whose join column is entirely null produces an empty set rather than a set containing
null.

*Why this settles three questions at once:* stated as set membership over non-null keys, the
question of how nulls behave in `Any`, in `None` and in `In` has one answer and no three-valued
logic arises anywhere.

**RL-11 (M).** The **empty key set** shall have both its identities stated and tested: `Any` over
an empty set returns **nothing**, `None` over an empty set returns **everything** that the rest of
the query allows.
*AC:* both are tests, because they are easy to get the wrong way round and neither fails loudly
when they are.

**RL-12 (M).** The outer query and every inner query shall run against **one catalogue snapshot**
(Q-11), and a failure of an inner query shall fail the whole query.
*AC:* a report never describes an inner query whose outer query then failed; an index created
between the inner and the outer query does not change the plan mid-flight, because the version was
fixed when the plan was made.

### 4.5 Diagnostics (group DG)

**DG-1 (M).** `QueryReport` shall gain: **how the order was produced**, **records retained by the
ordering stage**, **records skipped**, **records returned**, and whether the plan was
**streaming** — able to stop early — or not.
*AC:* every new figure appears in `ToString`, and each is asserted by at least one test that would
fail if the figure were computed from the wrong stage.

*Why "streaming" rather than "a `Take` stopped the walk early":* an index walk can stop and an
ordering stage cannot, so the earlier flag was meaningful for one plan and silently false for the
other. Reporting whether the plan could stop at all, and how many records the stage retained, says
something true of both. **The bounded heap does not change this**: it bounds memory and
materialisation, and every candidate is still examined, because an unseen record may outrank a
retained one. A plan with an ordering stage is reported as not streaming and memory-bounded, which
are two different facts and are reported as two.

**DG-2 (M).** A query carrying relation steps shall report the **inner report of each step**,
nested, rather than folded into the outer numbers.
*AC:* a relation query's report shows two page counts and two access paths, and a test asserts the
outer count excludes the inner.

**DG-3 (M).** The report shall remain part of the result, not a log line, and every query shall
continue to pass through `QueryService` so that the `QueryExecuted` event sees it.
*AC:* a relation query raises the event for the inner query as well as the outer, in that order.

**DG-3a (M).** A **failed or cancelled** execution shall also raise the event, once, with the
partial report it had reached.
*AC:* a cancelled query and a query that threw each produce exactly one event carrying the figures
accumulated up to the stop; a subscriber counting queries sees the same number as were started.

*Why this needs saying:* the diagnostics event is the only place every query of the engine is
observed. If it fires only on success, the expensive queries — the ones cancelled because they were
taking too long — are precisely the ones that leave no trace.

**DG-4 (M).** The execution shall be defined as an explicit **pipeline**, and every counter shall
be an event of a named stage rather than a number computed somewhere convenient:

| Stage | What it does | What it counts |
|---|---|---|
| candidate enumeration | walks the access path | pages read, records examined |
| predicate evaluation | conjuncts then residual, against the record on the page | records matched |
| ordering | index walk, bounded heap, or complete sort | order source, records retained |
| paging | skip and take, inside the ordering stage (PG-5) | records skipped, records returned |
| materialisation | only surviving records of the page become documents | documents materialised |
| report finalisation | closes the figures, after enumeration ends | elapsed |

*AC:* each counter is **owned by exactly one stage** and incremented nowhere else, asserted by a
test that would fail if it moved. Making a boundary a type is a good way to make ownership obvious
and is not required for its own sake.

**DG-5 (M).** `Run` shall **materialise the requested page** and return a **complete** report.
Laziness is internal to the pipeline and never reaches the caller.
*AC:* the report handed back has final figures for every counter, including elapsed and pages read;
`QueryService.QueryExecuted` fires once, after finalisation, with those figures.

*Why the result is not a lazy enumerable:* a report that finishes when the caller finishes
enumerating is a report whose numbers are untrue at the moment the diagnostics event carries them,
and `QueryExecuted` is the one place every query of the engine is observed. A streaming entry point
can be added later beside this one; it cannot replace it.

### 4.6 Non-functional (group NF)

**NF-1 (M).** A `Take(20)` over an ordered indexed column, on a collection of 100 000 records,
shall materialise 20 documents and read a number of pages bounded by the page window, not by the
collection.
*AC:* asserted on `DocumentsMaterialised` and `PagesRead`.

**NF-2 (M).** No new path shall materialise every document of a collection. The rule the engine
already holds itself to extends to ordering, paging and relations.
*AC:* every scenario of §5 asserts `DocumentsMaterialised` against an expected number, and none of
them is the collection size.

**NF-3 (S).** A relation filter whose inner query is selective shall cost **one inner query plus
one `In` access operation**, and the report shall say **how many index probes** that operation
made.
*AC:* the sum of the two page counts is asserted against a bound, and **three figures are reported
separately** rather than collapsed into one seek: the distinct key count, the number of index
probes, and the pages read.

*Why the earlier wording was wrong rather than loose:* a seek over N values is N descents of the
tree, one per value, and there is no page cache to make the later ones cheap. "One seek" understated
the cost of exactly the case the cap and RL-3a's second strategy exist for.

**NF-4 (M).** Execution shall be **cancellable**, and cancellation shall be propagated into every
inner query. It shall be observed **inside the long loops** — the page walk, the index walk and the
ordering stage — and not only between named stages, because a single stage can be the whole cost.
*AC:* a cancelled scan, ordering stage or semi-join stops within a bounded number of records rather
than at the end of the stage; cancelling an outer query cancels its inner queries, asserted by a
test.

**NF-4a (M).** A cancelled query shall **not** return as though it had succeeded. It ends as a
distinct cancelled outcome carrying the **partial report**, whether that is a separate result case
or a domain exception holding the report.
*AC:* a caller cannot mistake a cancelled page for a short page; the partial report is reachable
from the cancellation, because the cost of the abandoned work is the thing worth seeing.

*Why the earlier wording was wrong:* it said a cancellation returns a partial report, which reads as
an ordinary result with fewer records in it. A page of twelve from a cancelled query and a page of
twelve from a finished one would then be indistinguishable, and the difference is the entire point.

**NF-5 (M).** The suite shall include **property-based tests**, not only the scenarios of §5,
because paging and ordering have invariants that individual examples pass through by luck. The
invariants are:

1. **Pages partition the result.** Concatenating every page of an ordered query equals that query
   run whole, in the same order.
2. **Pages are duplicate-free and gap-free.** No record appears in two pages, and no matching
   record appears in none.
3. **The order is total.** Over generated data the comparator is antisymmetric and transitive, and
   no two distinct records compare equal.
4. **`Any` is set membership.** It returns exactly the outer records whose join key is in the
   projected set, and **`None` returns exactly the complement**.
5. **The index does not change the answer, for a query that asked for an order.** The same
   **explicitly ordered** query over the same data, with and without an index on the columns it
   names, returns identical records in an identical order. For an unordered query the property is
   over **sets and without `Take`**, because `Take` with no order means any N and is nondeterministic
   by decision (PG-4).

*AC:* each is a generated test over randomised data and schemas, run against a **fixed data and
catalogue snapshot** so a failing case can be replayed. The fifth is the most valuable and the least
obvious: it tests the planner against itself, and it is the only one of the five that would catch a
wrong access path returning plausible records. Its scoping is not a weakening — stated unscoped it
would be **false by design**, and a property test that fails for a reason the design intends teaches
a team to ignore it.

---

## 5. The scenarios that define "done"

**S-1. Ordered page, free.** `OrderBy(date).Skip(20).Take(20)` with an index on `date` and no
predicate, or a predicate on `date` itself. The order comes from the index walk, the ordering stage
retains nothing, 20 documents are materialised.

**S-1a. Predicate and order on different columns.** `Where(city = 'Lviv').OrderBy(date)
.Skip(20).Take(20)` with single-column indexes on each. There is no free answer: the planner either
seeks `city` and orders afterwards, or walks `date` in order and filters `city`, and OR-2a's rule
decides which. Both return the same records in the same order, and the plan says which was chosen.

*This scenario was the first draft's S-1, and it was unachievable as written.* It claimed the order
came free from a `date` index while filtering on `city`. The planner chooses one path, the only
conjunct names `city`, and nothing would have made it walk the `date` index — a range path needs an
ordered predicate on that column, and there is none. Serving both from one walk needs a composite
key over `(city, date, identity)`, which this engine's single-column indexes cannot express.

**S-2. Ordered page, bounded heap.** The same with no index on `date`. The records are filtered
first, the ordering stage keeps at most `Skip + Take` of them by the order and discards the rest as
it goes, and the page is materialised. The report says how the order was produced and how many
records the stage retained.

**S-3. Descending.** The same, descending. Correct order, produced by the ordering stage rather
than the walk, with the reason recorded (Q-3).

**S-4. Ties.** Ten thousand records share a date. Page five and page six neither repeat a record
nor skip one, because identity is the last key (Q-4).

**S-5. Relation, forward, small key set.** Expenses whose conference is in Lviv. One inner query
over `conferences`, an `In` on `expenses.conferenceId` executed as seeks, two reports, and the
probe count reported.

**S-5a. Relation, forward, large key set.** The same where most conferences match. The `In` is
executed as one pass over `expenses` against a hash set, reported as such, and it returns the same
records as S-5 would (RL-3a).

**S-6. Relation, reverse.** Conferences that have an expense over 500. One inner query over
`expenses`, and the outer path is whatever the planner chooses for the rewritten conjunct, which
for the identity column is the primary index (RL-9).

**S-7. Relation, none.** Conferences with no expenses at all. A full scan reported as an
anti-join, and correct.

**S-8. Everything at once.** A relation filter, two order columns, a skip and a take, over a
collection of 100 000 records, materialising exactly one page.

**S-9. Nulls.** A collection where some records have no value in the order column and some have no
value in the join column. The nulls order first ascending and last descending (OR-8), and the
records with a null join value are absent from `Any` and present in `None` (RL-10).

**S-10. Empty key set.** An inner query matching nothing. `Any` returns nothing; `None` returns
everything the rest of the query allows (RL-11).

### The ones that are not happy paths

**N-1. Skip with no order.** Refused at build time, naming the requirement.

**N-2. An order on a column that does not exist.** Refused at **plan** time, naming the column,
and identically from `Explain` and from `Run`, because both plan (QM-4a).

**N-3. An unknown relation.** Refused at **plan** time, naming it and listing what is declared.

**N-4. A relation step inside a relation step.** Refused, naming both.

**N-5. `All`.** Refused, with the reason rather than an empty result.

**N-6. The key set over its cap.** Fails before the outer query runs, with the relation, the size
in bytes and the cap in the message.

**N-6a. The ordering stage over its cap.** A complete sort larger than OR-7 allows fails before a
page is returned, with the stage, the size and the cap.

**N-7. A self-relation with no direction.** Refused at **plan** time, naming the collection,
because only the catalogue knows the relation's two sides are the same.

**N-7a. A relation step under an `Or`.** Refused at build time, naming the relation and the
position, because lifting it out of a disjunction would change what the query means (RL-3).

**N-8. `Take` beyond the end.** A page starting past the last record returns nothing, reports it,
and is not an error.

**N-9. The catalogue moves between planning and execution.** An index dropped or created after a
plan was made is caught by the plan's catalogue version (QM-2a): the execution fails with a typed
error naming the version it expected, or re-plans, and never silently takes a path that no longer
exists.

**N-10. Cancellation.** A long scan, a complete sort and a semi-join are each cancelled. Each stops
at the next stage boundary, returns a partial report rather than nothing, and an outer
cancellation reaches the inner query (NF-4).

**N-11. Arithmetic at the edges.** `Skip(int.MaxValue).Take(int.MaxValue)` builds, plans and
returns an empty page, without the sum wrapping to a negative window (QM-4b).

---

## 6. Risks

**R-1. No backward leaf chain.** `BPlusTree` leaves link forward only, so descending is a sort
whatever the column. Adding a previous pointer is a page-format change and a migration, not a
query change. *Recorded, not scheduled.*

**R-2. Deep offsets.** `Skip` is linear in the offset by construction. The seek-after cursor that
fixes it needs the total order of OR-1, which this plan establishes, so the fix is available later
without rework. *Accepted, and stated in PG-3 rather than discovered.*

**R-3. Sort memory.** The bounded heap of OR-3a removes this for an ordinary page: memory is a
function of `Skip + Take` and not of the match count. It remains for a **deep offset or a requested
total**, which still hold everything, and OR-7 caps those with a typed failure. *A spill to disk is
out of scope and is question 3 of §9.*

**R-4. Semi-join key sets.** An unselective inner query produces a large `In`, which is both memory
and a large number of index descents, one per value. RL-3a's second strategy answers the descents
by walking the outer collection once against a hash set, and Q-9's cap answers the memory. *Both the
crossover and the cap come from measurement, not taste.*

**R-5. No statistics, so no cost model.** The planner's rule stays a rule. A relation filter always
runs the inner query first, even where scanning the outer collection would have been cheaper.
*Visible in the two reports, which is the condition for fixing it later.*

**R-6. The crossover of RL-3a is a second rule with no statistics behind it either.** Choosing
between seeks and a pass needs the size of the outer collection, which the catalogue knows, and the
selectivity of the key set, which nothing does. *Measured once and configured, and reported on every
query, so a wrong crossover is visible rather than inferred.*

**R-8. Single-column indexes.** A selective predicate on one column and an order on another cannot
both be served by one walk, so one of them is always paid for (S-1a, OR-2a). This is not a defect of
the query mechanism and no amount of planning removes it. *The cost table of PG-3a shows what each
choice costs, which is the condition for deciding later whether composite keys are worth their
format change.*

**R-7. Laziness reaches further than the query.** Making the walk lazy changes when pages are read
relative to when a caller holds a transaction. `DG-5` keeps that inside `Run`, which is what stops
it becoming a lifetime question for every consumer. *If a streaming entry point is ever added, that
question comes back with it.*

---

## 7. Development plan

**Phase 1 — The request, the builder and the plan boundary.** The immutable request with copied
collections, a builder whose every method returns a new builder, the split between build-time and
plan-time refusals, and the catalogue version a plan carries.
*Exit:* N-1, N-4, N-5, N-7 and N-11 fail at build time and N-2 and N-3 at plan time, each with its
message; `Explain` and `Run` agree against one catalogue version and N-9 is caught rather than
survived; no existing query test has changed.

**Phase 2 — Ordering.** The order model with a directed tiebreaker, the prefix rule for the free
case, the bounded heap, the cross-type and null contracts, and the ordering cap.
*Exit:* S-1 to S-4 and S-9 pass, OR-2's "nothing sorted" is asserted rather than assumed, the heap
is asserted on its high-water mark, and N-6a fails with its three numbers.

**Phase 3 — The shared pipeline and paging.** The lower-level candidate pipeline both entry points
sit on, a lazy walk, `Take` stopping it, `Skip` without materialisation, and the paging figures.
*Exit:* PG-1 and PG-2 are asserted on page counts and document counts, not on timings; the old
entry point still passes its tests and neither entry point is implemented by filtering the other's
result.

**Phase 4 — Relations.** Direction by role, the key projection, the rewrite, both `In` strategies,
`NotIn` as set membership, null and empty-set semantics, the cap, and nested reports.
*Exit:* S-5, S-5a, S-6, S-7 and S-10 pass, N-6 fails with its three numbers, no plan reaching the
executor contains a relation step, and a relation query's two reports are separately correct.

**Phase 5 — The whole thing.** S-8, cancellation, the property tests, the non-functional
assertions, and the documentation of the builder.
*Exit:* every scenario passes, the five invariants of NF-5 hold over generated data, N-10 returns
partial reports rather than nothing, and NF-2 holds across all of them.

---

## 8. Development steps, as prompts

**1.1 The request**
> Read requirements QM-1, QM-1a and QM-3 and decisions Q-1 and Q-11. Add an immutable request
> record carrying the NormalizedQuery, the id list, the order, Skip, Take and the relation steps,
> copying the nested collections rather than wrapping them. Leave NormalizedQuery untouched. Done
> when: the record exposes no mutable member, a reflection test asserts it, mutating the builder
> after building changes nothing in what was built, and the existing Query(NormalizedQuery, ids)
> still compiles and passes its tests unchanged.

**1.2 The builder and its refusals**
> Read requirements QM-1a, QM-2, QM-2a, QM-2b, QM-4, QM-4a, QM-4b and QM-5 and decision Q-11. Add the
> fluent builder on DbEntities<T> with Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending,
> Skip, Take, the relation step, Run and Explain. Every method returns a new builder. Refusals that
> need no catalogue are raised when the request is built; refusals that need one are raised when it
> is planned, identically from Explain and from Run, the self-relation direction among them because
> only the catalogue knows a relation's two sides. Explain returns a concrete immutable plan that Run
> can execute directly. Planning and execution hold a catalogue read lease, so a version comparison
> is never followed by an unprotected read; a plan handed back after the version moved is refused
> rather than re-planned. Hold Skip, Take and their sum in 64-bit saturating arithmetic and allocate
> nothing at a capacity taken from them. Done when: N-1, N-4, N-5, N-7a and N-11 fail at build time
> and N-2, N-3 and N-7 at plan time, each naming what was wrong; N-9 is refused by the version; a
> schema change during a query waits for the lease; Explain reads no data page and its plan executes
> unchanged; and Take(0) returns an empty result with a report.

**2.1 The order model**
> Read requirements OR-1, OR-5 and OR-8 and decision Q-4. Add the order as a sequence of column and
> direction pairs with record identity appended as the final tiebreaker, in the direction of the
> last declared column, applied after the conjuncts and the residual. One comparator object, shared
> by the ordering stage and by anything that later compares two positions. Nulls sort before every
> value ascending and after it descending, which is what the key encoding already does. Done when:
> two records equal on every declared column keep the same relative position across runs, a
> descending last column gives descending identity order, a query filtering to five records out of a
> million orders five, and nulls land in the same place whether the order came from a walk or a
> stage.

**2.2 Order from the index walk**
> Read requirements OR-2 and OR-2a and decisions Q-3 and Q-14. Take the order from the walk whenever
> the **requested order is a prefix of the index key order**, directions included and the identity
> tiebreaker counted as part of the request. Get the direction of that rule right: the key must
> refine the request, so a key of (date, identity) satisfies a request for (date, identity) and does
> **not** satisfy one for (date, cost, identity). Then let the planner choose an ordered index walk
> for the order's sake, applying the predicate as a filter, and choose between that and the
> predicate's own path by the stated rule. Done when: an ordered query the key satisfies retains
> nothing in the ordering stage, a query asking for one column more than the key resolves reports a
> stage instead, and S-1a produces a plan naming which of the two paths it chose while both choices
> return the same records in the same order.

**2.3 Order by sorting keys, not documents**
> Read requirements OR-3, OR-3a, OR-3b, OR-4, OR-6, OR-6a and OR-7 and decision Q-12. Order over the order
> columns and the record address, never over documents, and materialise only the requested page.
> Where the page is bounded keep at most Skip + Take records by the order and discard the rest as
> you go; only a deep offset or a requested total holds everything, and that is what the memory cap
> governs. Descending uses the stage, because the leaf chain runs forward only; record that reason in
> the test so that the test which must change when a backward walk exists is the one that recorded
> the limitation. Put the cross-type order in the contract and test every supported pair, including
> the two that surprise people: signed and unsigned integers sort in separate runs, and 5 as an
> integer is not the same key as 5.0. Define the comparator in terms of the encoded key order and
> never fall back on a CLR comparison, which would agree for most values and disagree for negative
> zero, NaN, decimals equal at different scales and Guid. Report the stage as memory-bounded and
> **not streaming**, because every candidate is still examined. Done when: ordering 100 000 matching
> records by an unindexed column and taking 20 materialises 20 documents, retains 20 in the stage,
> reports examining all 100 000 and reports the plan as not streaming; a complete sort over the cap
> fails with its three numbers; the stage and an index walk agree on every supported type; and a
> mixed-type column orders deterministically and says so.

**3.1 A walk that can stop**
> Read requirements PG-1, PG-2, PG-4, PG-5 and DG-5 and decisions Q-7 and Q-12. Make the executor's
> walk lazy so that Take
> stops it and Skip discards without materialising. Do not turn this into a filter over the
> finished list — the executor's whole premise is that only a surviving record becomes a document,
> and paging has to keep that. Done when: Take(20) over 100 000 records with no predicate reads the
> pages of the first 20 and no more, Skip(1000).Take(20) reports 20 documents materialised, Run
> hands back a finished report rather than a lazy enumerable, and Take(5) with no order returns five
> records that all satisfy the predicate while Skip(5) with no order does not build.

**3.2 The paging figures**
> Read requirements PG-3, PG-3a, PG-6, DG-1, DG-3a and DG-4. Define the pipeline as explicit stages —
> candidate enumeration, predicate evaluation, ordering, paging, materialisation, report
> finalisation — and make every counter an event of one named stage. Add records retained, records
> skipped, records returned, whether the plan was streaming, and how the order was produced, to
> QueryReport and its ToString. Make the total count something a caller asks for, with its own cost
> shown. Raise the event once for a failed or cancelled execution too, carrying the partial figures,
> since the expensive queries are exactly the ones that would otherwise leave no trace. Write the cost
> table of PG-3a and make each reachable row a test, the ordered-walk row included, where an
> unselective predicate costs the whole index rather than the page window. Done when: each counter is
> owned by exactly one stage and a test would fail if it moved, the unreachable row of the table is
> asserted unreachable, a cancelled query raises exactly one event, and a paged query that did not
> ask for a total does not compute one.

**4.1 Direction and refusals**
> Read requirements RL-1, RL-2, RL-3, RL-7, RL-8 and RL-12. Resolve a relation step against the
> RelationCatalog in whichever direction the queried collection sits, naming direction by role — to
> the target, or to the source — rather than by a flag, and requiring it only for a self-relation,
> which is a plan-time refusal because only the catalogue knows the two sides are the same. Accept a
> relation step only in an AND-conjunctive position and refuse one under an Or or a Not at build
> time, naming the relation and the position: lifting a step out of a disjunction changes what the
> query means. Run the outer query and every inner query under one catalogue lease. Refuse All with a
> NotSupportedException that says why, and refuse a relation step inside a relation step naming both.
> Done when: the same declared relation resolves from either side, N-7 is refused at plan time and
> N-7a at build time, both directions of a self-relation have a worked example in the builder's
> documentation, and each refusal is a test with its message.

**4.2 The semi-join**
> Read requirements RL-3a, RL-3b, RL-3c, RL-4, RL-6, RL-9, RL-10 and RL-11 and decisions Q-5, Q-9
> and Q-13.
> Make the inner query a key projection rather than a request: a predicate and one column, with no
> order, no paging and no materialisation. Project the far column of the matching records,
> deduplicate, exclude nulls from the set, and rewrite the step as a function on the normalised
> query — remove it from the residual, add an In conjunct to the conjuncts. Execute the In by one of
> two strategies, comparing the estimated probes — distinct keys times index height — against the
> pages of the outer collection: seeks below the crossover, one membership pass above it, reported as
> its own access path rather than as a seek. Report the distinct key count, the probe count and the
> pages read as three figures. Hold the keys in a compact purpose-built set and cap the bytes that
> set allocates, not the sum of the keys, because on short keys the per-entry overhead is the larger
> half. Send the rewritten conjunct to the planner like any other, with In among the shapes the
> planner declares it can answer and no special case for the identity column. Done when: S-5, S-5a,
> S-6 and S-10 pass, the inner query reports zero documents materialised, deduplication is asserted,
> both strategies return the same records for the same query, the invariant that no plan reaching the
> executor contains a RelationStepExpression is checked recursively over conjuncts and residual, and
> N-6 fails before the outer query runs.

**4.3 None, and what it costs**
> Read requirements RL-5 and RL-10 and decision Q-8. Add NotIn to the comparison operators, make it
> non-indexable so the planner never chooses a path by it, and evaluate it as a filter against the
> record on the page. Define it as set membership and not as SQL's three-valued logic: a record whose
> near column is null satisfies NotIn, because its key is in no set. Done when: a query whose only
> condition is a None step reports a full scan with the anti-join as the reason, the same query with
> a second indexable condition takes the index for that condition, and the null case is a test,
> because the intuition people arrive with is the other one.

**4.4 Two reports, not one**
> Read requirements DG-2 and DG-3 and decision Q-10. Carry each inner report inside the outer one
> and raise QueryExecuted for both, inner first. Done when: a relation query's report shows two
> access paths and two page counts, and a test asserts the outer count excludes the inner.

**5.1 The whole thing**
> Read section 5 and requirements NF-1, NF-2, NF-3, NF-4, NF-4a and NF-5. Make execution
> cancellable, with the token observed inside the page walk, the index walk and the ordering stage
> rather than only between stages, propagated into every inner query, and ending in a distinct
> cancelled outcome carrying the partial report — never in an ordinary result that looks like a short
> page. Add the five property tests over generated data and schemas, against a fixed data and
> catalogue snapshot so a failure can be replayed: pages partition the ordered result, pages are
> duplicate-free and gap-free, the order is total, Any is set membership and None its complement,
> and an explicitly ordered query returns identical records in identical order with and without an
> index — scoped to ordered queries, because for an unordered Take the property is false by design.
> Run S-1 to S-10 and N-1 to N-11 and report what each costs. Done when: every scenario passes, every
> invariant holds, a cancelled query cannot be mistaken for a finished one, every scenario asserts
> documents materialised against an expected number that is not the collection size, and the
> builder's documentation states what a deep Skip costs and why.

---

## 9. Still open

1. **The semi-join cap and the strategy crossover** (Q-9, RL-3a). One decides when a relation query
   refuses and the other decides how it runs, and neither can be chosen from taste. Settle both in
   step 4.2, from the memory a key set of that size takes and from where seeks stop beating a pass.
2. **Whether `Take` alone should imply an order.** Today it means "any n", which is right for a
   safety cap and surprising for anything else. Settle from how callers use it, not before.
3. **Whether a complete sort should spill.** OR-3a removes the ordinary case, so this is now only
   about a deep offset or a requested total. OR-7 caps them; a spill is a phase of its own if a
   benchmark asks for one.
4. **The seek-after cursor** (R-2). The total order of OR-1, now with a defined tiebreaker
   direction, is what it needs and this plan establishes it; whether it belongs in the engine or
   above it is a separate question.
5. **The rule of OR-2a**, which decides between the predicate's path and an ordered walk. It is
   written as a rule because there are no statistics, and where that rule is wrong it is wrong
   visibly. What is open is the threshold inside it, which comes out of step 2.2.
6. **A streaming entry point beside `Run`** (DG-5). The pipeline is lazy inside, so exposing it is
   cheap; what is not cheap is a report whose figures are untrue until the caller stops enumerating.
   If it is added, it needs its own reporting contract rather than this one.
7. **Whether composite indexes are worth adding for this.** They are the only thing that makes S-1a
   free, and they change the key format, the catalogue and the planner at once. Out of scope here
   and recorded as R-8, so that the cost of not having them is visible in the cost table rather than
   discovered in a profile.

---

## Appendix: what changed in draft 3

Thirty review points, twenty-nine distinct topics, and six of them corrections to things this
document asserted rather than omitted. Those six are worth separating, because a requirements
document that is wrong is more expensive than one that is thin.

**The opening scenario was unachievable.** S-1 filtered on `city` and claimed a free order from an
index on `date`. The planner chooses one path, the only conjunct names `city`, and nothing would
have made it walk the `date` index. **Q-14** lets an order choose an access path, **OR-2a** gives
the rule for choosing between that and the predicate's path, and **S-1a** replaces the claim with
the real trade. **R-8** records why neither is free: single-column indexes cannot serve a selective
predicate on one column and an order on another from one walk.

**The order-prefix rule was backwards.** It said the index key order must be a prefix of the
requested order. A walk satisfies a request only when the key **refines** it, so the request must be
a prefix of the key. Written the wrong way round, a request for `(date, cost, identity)` against a
key of `(date, identity)` reads as satisfied, and the query returns the wrong sequence while
reporting that nothing needed ordering. **OR-2** now has it the right way and says so.

**The bounded heap was described as making sorted plans streaming.** It does not: every candidate is
still examined, because an unseen record may outrank a retained one. **OR-3a** and **DG-1** now
report two separate facts, memory-bounded and not streaming.

**A version check is not a snapshot.** Comparing a catalogue version and then reading is a race over
the length of the whole query. **QM-2b** adds a read lease held across planning and execution, and
**QM-2a** settles the open question in favour of refusing a stale plan rather than re-planning it,
because a caller who held a plan measured a cost.

**A self-relation cannot be caught at build time.** Whether a relation's two sides are the same
collection is a fact about the catalogue, and the builder holds only a name. That refusal moved to
plan time in **QM-4a**, which is the same class of error this document fixed once already.

**One property test was false by design.** "The same query with and without an index returns
identical records in an identical order" cannot hold for an unordered `Take`, which means any N.
**NF-5** scopes it to explicitly ordered queries and compares sets otherwise.

**One more correction came out of the table rather than the headlines.** Lifting a relation step out
of the predicate is equivalent only where the step was required on its own. Under an `Or` it is not,
so removing it changes what the query means. **RL-3** now refuses a relation step in any position
but an AND-conjunctive one, at build time, which is a soundness rule and not a convenience.

**The rest sharpened requirements that were right.** A cap on encoded-key bytes is not a memory cap,
because per-entry overhead exceeds short keys, so **RL-6** caps what the set allocates. A cancelled
query must not look like a short page, so **NF-4a** gives it a distinct outcome carrying the partial
report, and **DG-3a** makes sure the expensive queries are the ones that do leave a trace. The
membership pass is its own access path rather than a seek by another name (**RL-3c**). The
comparator is defined by the encoded key order and never by a CLR comparison (**OR-6a**), which
would agree for most values and disagree for exactly the four the key encoder was written to handle.
Nothing is allocated at a capacity taken from `Skip + Take` (**QM-4b**). And the old query entry
point keeps working without being made to carry the new semantics: both now sit on one lower-level
pipeline (**QM-3**), since an API that materialises every match can express neither a page nor an
order.

---

## Appendix: what changed in draft 2

Thirty review comments. Twenty-eight held, and eleven of them turned out to be four changes rather
than eleven.

**Three comments were one broken triangle.** Build-time validation, the claim that `Explain`'s plan
equals `Run`'s, and the scenario where an index disappears between them cannot all be true. **Q-11**
separates the request, the plan and the execution, so names validate at plan time, a plan carries the
catalogue version it was made against, and execution re-checks it. **QM-4** and **QM-4a** split the
refusals along that line, **QM-2a** carries the version, and **RL-12** fixes one snapshot over the
outer query and every inner one.

**Two comments were one missing idea.** Both the sort-memory comment and the "Take cannot stop a
sort" comment are answered by not sorting everything: **OR-3a** keeps at most `Skip + Take` records
in a bounded heap, which makes the memory a function of the page and makes early termination
meaningful for ordered plans. A complete sort now happens only for a deep offset or a requested
total, and **OR-7** caps those, which also removes an inconsistency where the semi-join had a cap
requirement and the sort had only a risk note.

**Two more were one overstatement.** An `In` over N values is N descents of the tree, not one seek,
against an engine with no page cache. **NF-3** now reports the probe count, and **RL-3a** adds the
second strategy: above a crossover, one pass over the outer collection against a hash set instead of
N random descents.

**Three were one semantics question.** Nulls in `Any`, nulls in `None` and null ordering have a
single answer that avoids three-valued logic entirely. **RL-10** defines the join as set membership
over non-null keys, **RL-5** says `NotIn` is membership and not SQL, and **OR-8** takes null ordering
from the key encoding, which is the only choice that lets an index walk satisfy the order.

**Three shared a pipeline.** **DG-4** names the stages and attaches every counter to exactly one of
them, which settles the metric ambiguity as a side effect, and **DG-5** keeps laziness inside `Run`,
because a report that completes when the caller stops enumerating is untrue at the moment the
diagnostics event carries it.

**One premise was wrong for this engine.** Composite indexes do not exist: the catalogue creates and
finds by one collection and one column. So **OR-2** was not too narrow. It is now stated as the
general prefix rule anyway, with a note that it currently reduces to the case the first draft wrote
directly, because writing the rule costs nothing and is already right if composite indexes ever land.

**One comment was answered rather than open.** `Take` without an order is decided, not
underspecified. What was missing is that nondeterminism cannot be tested: **PG-4** now states the
invariant that does hold, which is that every returned record satisfies the predicate and the count
is the lesser of N and the matches.

**The rest were straightforwardly right.** A directed identity tiebreaker (**OR-1**), the cross-type
order as a contract with its two surprises (**OR-6**), direction by role with self-relation examples
(**RL-2**), the rewrite stated as a function with an assertion that no relation step reaches the
executor (**RL-3**), the inner query as a key projection that cannot express an order (**RL-4**), the
cap in bytes (**RL-6**), the empty-set identities (**RL-11**), the access path left to the planner
instead of a special case (**RL-9**), copied collections (**QM-1**), a builder whose methods return
new builders (**QM-1a**), overflow-safe arithmetic (**QM-4b**), cancellation with a partial report
(**NF-4**), the cost table per path and order source (**PG-3a**), and five property tests
(**NF-5**), of which the last is the most valuable: the same query with and without an index must
return identical records in identical order, which tests the planner against itself.
