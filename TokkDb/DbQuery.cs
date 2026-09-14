using System.Collections.Immutable;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages.Query;

namespace TokkDb;

//Q-1: the entry point of an entity query. It collects a predicate, an order, Skip and Take and
//relation steps, and produces one immutable QueryRequest for the planner to read.
//
//A persistent value (QM-1a): every method returns a new query and leaves this one as it was, so
//a base query can be extended two ways without either seeing the other's additions, and a request
//built from it can never be changed by what is done to the query afterwards. That is a property of
//the type rather than of careful use.
//
//Nothing is refused while the query is being assembled beyond a misuse of the builder itself — a
//missing argument, or a ThenBy with nothing to follow. The refusals a request can raise about
//itself come when it is built — by Build, Explain or Run — because only then is the whole of it
//known: Skip before OrderBy is a legal way to write an ordered page. The refusals that need the
//catalogue come when it is planned.
//
//What a query costs, stated rather than implied:
//
//  An order is free where an index walk yields it (OR-2): a single column, ascending, indexed,
//  and reached by a walk of that index. Everything else is an ordering stage over keys and
//  addresses, never documents (OR-3). With a Take the stage keeps at most Skip + Take records
//  and discards the rest as they arrive (OR-3a); without one it holds every match (OR-3b), and
//  QueryOptions.OrderingStageCapBytes fails the query rather than the process (OR-7). Descending
//  is always the stage, because the leaf chain of an index runs one way only (Q-3, R-1).
//
//  Skip costs what it skips (PG-3, R-2). The records before the page are still examined — for the
//  predicate, and for the order — and a skipped record costs the fields those name, and no
//  document (PG-2). The cost is therefore linear in the offset by construction: Skip(10000) over an
//  index walk examines 10 000 matching records before the page, and over an ordering stage it
//  holds 10 000 + Take keys in the heap. The report says how many were skipped. The fix for a deep
//  offset is a seek-after cursor, which the total order of OR-1 makes possible and this builder
//  does not yet expose (R-2, §9 of the plan).
//
//  Take without an order means any N matching records (PG-4): every returned record satisfies the
//  predicate and the count is the lesser of N and the matches, and which records is unspecified.
//  Skip without an order is refused (Q-2), because page two of an arbitrary handful is not defined.
//
//  A relation step is a semi-join (Q-5): one inner query over the far collection, projecting the
//  join column's keys of the records matching the inner predicate, and then an In over those keys
//  on the near column, executed by seeks or by one membership pass whichever the crossover
//  favours (RL-3b). The report carries the inner query's report nested (DG-2). None is the
//  complement — set membership, not SQL's NOT IN — and is always a scan of the near collection
//  unless another condition takes an index (Q-8, RL-5). A record whose join column is null is
//  related to nothing: absent from Any, present in None (RL-10).
//
//Direction across a relation from a collection to itself (RL-2), with the relation
//TaskParent: Task.ParentId -> Task.Id:
//
//  tasks.Query().WhereRelated("TaskParent", RelationQuantifier.Any, parentIsUrgent,
//      RelationDirection.ToTarget)
//    — the tasks whose parent matches: the step goes from a task's ParentId to the record whose
//      Id it holds, which is the relation's target side.
//
//  tasks.Query().WhereRelated("TaskParent", RelationQuantifier.Any, subtaskIsOverdue,
//      RelationDirection.ToSource)
//    — the tasks with a matching subtask: the step goes from a task's Id to the records whose
//      ParentId holds it, which is the relation's source side.
//
//  Where the queried collection is on only one side, the direction is inferred and need not be
//  given; a self-relation with no direction is refused when the query is planned, because only
//  the catalogue knows the two sides are the same collection (QM-4a).
public sealed class DbQuery<T> {
  private readonly DbEntities<T> _entities;
  private readonly State _state;

  internal DbQuery(DbEntities<T> entities)
    : this(entities, new State(NormalizedQuery.Everything, null, [], 0, null, [], false, null)) { }

  private DbQuery(DbEntities<T> entities, State state) {
    _entities = entities;
    _state = state;
  }

  //Conjoined with any predicate already given: two Wheres are one AND. The constants are copied
  //now rather than at Build, because a constant list the caller still holds could otherwise change
  //this query between the two.
  public DbQuery<T> Where(IExpression predicate) {
    ArgumentNullException.ThrowIfNull(predicate);
    var added = QueryNormalizer.Normalize(predicate);
    var current = _state.Predicate;
    var copied = added.Conjuncts.Select(conjunct => conjunct with { Constants = [.. conjunct.Constants] });
    var conjoined = new NormalizedQuery(ImmutableArray.CreateRange([.. current.Conjuncts, .. copied]),
      Conjoin(current.Residual, added.Residual));
    return With(_state with { Predicate = conjoined });
  }

  //The records with these identities and no others. A second call narrows to the identities both
  //name, as a second Where does, and an empty list is a query for no record rather than for every
  //record. Copied now, because the caller's list may go on changing after this returns.
  public DbQuery<T> WhereIdIn(IEnumerable<Ulid> ids) {
    ArgumentNullException.ThrowIfNull(ids);
    var named = ids.ToImmutableArray();
    if (_state.Ids is { } existing) {
      var both = named.ToHashSet();
      named = existing.Where(both.Contains).ToImmutableArray();
    }
    return With(_state with { Ids = named });
  }

  //Narrows the query by a declared relation (RL-1): Any keeps the records with at least one related
  //record matching the inner predicate, None the records with none. Direction may be left to the
  //catalogue except across a relation from a collection to itself, where it has to be stated; the
  //two worked examples are in the notes above. All is refused (RL-7): a near column holds one value,
  //so there is nothing for it to quantify over.
  public DbQuery<T> WhereRelated(string relationName, RelationQuantifier quantifier, IExpression inner = null,
      RelationDirection? direction = null) {
    var step = new RelationStep(relationName, quantifier, QueryNormalizer.Normalize(inner), direction);
    return With(_state with { RelationSteps = _state.RelationSteps.Add(step) });
  }

  //Starts the order again from this column; ThenBy extends it. Record identity is always the last
  //key, in the direction of the last column (OR-1), so every order is total and its pages partition
  //the result.
  public DbQuery<T> OrderBy(string columnName) {
    return With(_state with { Order = [new OrderColumn(columnName)] });
  }

  public DbQuery<T> OrderByDescending(string columnName) {
    return With(_state with { Order = [new OrderColumn(columnName, OrderDirection.Descending)] });
  }

  public DbQuery<T> ThenBy(string columnName) {
    return ThenBy(columnName, OrderDirection.Ascending, nameof(ThenBy));
  }

  public DbQuery<T> ThenByDescending(string columnName) {
    return ThenBy(columnName, OrderDirection.Descending, nameof(ThenByDescending));
  }

  //Q-2: needs an order, and a query with Skip and none does not build. The records before the page
  //are still examined, so a deep offset costs what it skips (PG-3, R-2) — see the notes above.
  public DbQuery<T> Skip(long count) {
    return With(_state with { Skip = count });
  }

  //A cap on the page. Without an order it means any N matching records (PG-4). Take(0) is an empty
  //page with a report, not the absence of a limit (QM-5). With an order it also bounds the ordering
  //stage to Skip + Take records (OR-3a).
  public DbQuery<T> Take(long count) {
    return With(_state with { Take = count });
  }

  //PG-6: asks for the count of every matching record beside the page, at a cost reported on its
  //own. A plan that stops at the page walks the rest to count it; one that examined everything
  //already knows.
  public DbQuery<T> IncludeTotal() {
    return With(_state with { IncludeTotal = true });
  }

  //How the query may be answered — the caps, the In crossover, a forced choice — as distinct from
  //what it asks. Not part of the request; carried on the plan it produces.
  public DbQuery<T> WithOptions(QueryOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return With(_state with { Options = options });
  }

  //The request as it stands, with every refusal that needs no catalogue raised (QM-4).
  public QueryRequest Build() {
    IEnumerable<Ulid> ids = _state.Ids is { } named ? named : null;
    return new QueryRequest(_state.Predicate, ids, _state.Order, _state.Skip, _state.Take, _state.RelationSteps,
      _state.IncludeTotal);
  }

  //The plan the request would run by, against the catalogue as it is now. Reads no data page, and
  //the plan it returns can be handed to DbEntities.Run as it is.
  public QueryRequestPlan Explain() {
    return _entities.Explain(Build(), _state.Options);
  }

  //Plans and runs the request under one catalogue lease, and returns the page with a finished
  //report (DG-5). A cancelled run ends in a QueryCancelledException carrying the partial report,
  //never in a short page (NF-4a).
  public DbQueryResult<T> Run(CancellationToken cancellation = default) {
    return _entities.Run(Build(), _state.Options, cancellation);
  }

  private DbQuery<T> ThenBy(string columnName, OrderDirection direction, string method) {
    if (_state.Order.IsEmpty) {
      throw new InvalidOperationException(
        $"{method} extends an order, and this query has none yet. Start one with {nameof(OrderBy)} or " +
        $"{nameof(OrderByDescending)}.");
    }
    return With(_state with { Order = _state.Order.Add(new OrderColumn(columnName, direction)) });
  }

  private DbQuery<T> With(State state) {
    return new DbQuery<T>(_entities, state);
  }

  private static IExpression Conjoin(IExpression left, IExpression right) {
    return (left, right) switch {
      (null, _) => right,
      (_, null) => left,
      _ => new AndExpression([left, right])
    };
  }

  private sealed record State(
    NormalizedQuery Predicate,
    ImmutableArray<Ulid>? Ids,
    ImmutableArray<OrderColumn> Order,
    long Skip,
    long? Take,
    ImmutableArray<RelationStep> RelationSteps,
    bool IncludeTotal,
    QueryOptions Options);
}
