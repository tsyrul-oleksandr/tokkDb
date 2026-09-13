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
public sealed class DbQuery<T> {
  private readonly DbEntities<T> _entities;
  private readonly State _state;

  internal DbQuery(DbEntities<T> entities)
    : this(entities, new State(NormalizedQuery.Everything, null, [], 0, null, [])) { }

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
  //catalogue except across a relation from a collection to itself, where it has to be stated.
  public DbQuery<T> WhereRelated(string relationName, RelationQuantifier quantifier, IExpression inner = null,
      RelationDirection? direction = null) {
    var step = new RelationStep(relationName, quantifier, QueryNormalizer.Normalize(inner), direction);
    return With(_state with { RelationSteps = _state.RelationSteps.Add(step) });
  }

  //Starts the order again from this column; ThenBy extends it.
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
  //are still examined, so a deep offset costs what it skips (R-2).
  public DbQuery<T> Skip(long count) {
    return With(_state with { Skip = count });
  }

  //A cap on the page. Without an order it means any N matching records (PG-4). Take(0) is an empty
  //page with a report, not the absence of a limit (QM-5).
  public DbQuery<T> Take(long count) {
    return With(_state with { Take = count });
  }

  //The request as it stands, with every refusal that needs no catalogue raised (QM-4).
  public QueryRequest Build() {
    IEnumerable<Ulid> ids = _state.Ids is { } named ? named : null;
    return new QueryRequest(_state.Predicate, ids, _state.Order, _state.Skip, _state.Take, _state.RelationSteps);
  }

  //The plan the request would run by, against the catalogue as it is now. Reads no data page, and
  //the plan it returns can be handed to DbEntities.Run as it is.
  public QueryRequestPlan Explain() {
    return _entities.Explain(Build());
  }

  //Plans and runs the request under one catalogue lease, and returns the page with a finished report.
  public DbQueryResult<T> Run() {
    return _entities.Run(Build());
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
    ImmutableArray<RelationStep> RelationSteps);
}
