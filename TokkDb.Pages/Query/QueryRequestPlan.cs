using System.Collections.Immutable;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Values;

namespace TokkDb.Pages.Query;

//Q-11: a request resolved against one version of the catalogue.
//
//A concrete value rather than a promise to plan (QM-2). Explain hands one back, and Run executes
//it as it stands; the version it carries is what Run checks it against, under the lease, before
//it reads anything (QM-2a). Only the planner makes one, so a plan that exists was checked against
//the catalogue it names.
//
//What a plan can and cannot know: the path the outer collection is reached by, and how the order
//is produced, are decided here. Which of the two In strategies a relation step runs by is not
//(RL-3b): it depends on how many keys the inner query projects, which is known only when the
//plan runs, so the plan names the seek and the executor may swap it for a membership pass. The
//report says which happened.
public sealed record QueryRequestPlan {
  internal QueryRequestPlan(string collectionName, QueryRequest request, CatalogLease lease, QueryOptions options,
      NormalizedQuery rewritten, QueryPlan access, string pathReason, OrderSource orderSource, string orderReason,
      ImmutableArray<RelationStepPlan> relationSteps) {
    CollectionName = collectionName;
    Request = request;
    CatalogVersion = lease.Version;
    Catalog = lease.Owner;
    Options = options;
    Rewritten = rewritten;
    Access = access;
    PathReason = pathReason;
    OrderSource = orderSource;
    OrderReason = orderReason;
    RelationSteps = relationSteps;
  }

  public string CollectionName { get; }

  //What was asked. Kept so that a caller whose plan went stale can plan it again in one call.
  public QueryRequest Request { get; }

  public long CatalogVersion { get; }

  //How the plan was allowed to answer: the caps, the crossover and any forced choice.
  public QueryOptions Options { get; }

  //The predicate as the planner saw it: the request's conjuncts and residual with the conjunct of
  //every relation step added (RL-3a). The executor binds the projected keys into it and applies
  //the planner's filter rule again, because a bound set can make an exact seek inexact.
  internal NormalizedQuery Rewritten { get; }

  //How the queried collection is reached, and what is re-checked against each record it yields.
  public QueryPlan Access { get; }

  //OR-2a: why this path and not the other, where an order gave the planner two to choose from.
  public string PathReason { get; }

  //OR-2, OR-2a, OR-3a and OR-3b: how the order will be produced, and why.
  public OrderSource OrderSource { get; }
  public string OrderReason { get; }

  public ImmutableArray<RelationStepPlan> RelationSteps { get; }

  public ImmutableArray<OrderColumn> Order => Request.Order;
  public long Skip => Request.Skip;
  public long? Take => Request.Take;

  //DG-1: whether the execution can stop once the page is full. A walk can; an ordering stage
  //cannot, because an unseen record may outrank a retained one.
  public bool IsStreaming => OrderSource is OrderSource.None or OrderSource.IndexWalk;

  internal CatalogLock Catalog { get; }

  public override string ToString() {
    var description = Access.ToString();
    foreach (var step in RelationSteps) {
      description += $", {step}";
    }
    if (Request.IsOrdered) {
      description += $", ordered by {string.Join(", ", Order)} ({OrderReason})";
    }
    if (Skip > 0) {
      description += $", skipping {Skip}";
    }
    if (Take is { } take) {
      description += $", taking {take}";
    }
    if (Request.IncludeTotal) {
      description += ", with a total";
    }
    return $"{description}; path: {PathReason} (catalogue version {CatalogVersion})";
  }
}

//RL-1 and RL-2: a relation step with its relation resolved — which side of it the queried
//collection is on, and so which of its columns is narrowed and which collection is read to do it
//— and the conjunct the step becomes (RL-3a).
public sealed record RelationStepPlan {
  internal RelationStepPlan(RelationStep step, RelationDirection direction, string nearColumn,
      ValueTypeEnum nearColumnType, string farCollection, string farColumn, QueryPlan inner, KeySet keys,
      QueryPredicate conjunct) {
    Step = step;
    Direction = direction;
    NearColumn = nearColumn;
    NearColumnType = nearColumnType;
    FarCollection = farCollection;
    FarColumn = farColumn;
    Inner = inner;
    Keys = keys;
    Conjunct = conjunct;
  }

  public RelationStep Step { get; }

  //Stated or inferred; resolved either way.
  public RelationDirection Direction { get; }

  //The column of the queried collection the step narrows, and its declared type.
  public string NearColumn { get; }
  public ValueTypeEnum NearColumnType { get; }

  public string FarCollection { get; }
  public string FarColumn { get; }

  //How the far collection is reached for the step's predicate, planned under the same lease as
  //the query it narrows (RL-12). The executor runs it as a key projection (RL-4).
  public QueryPlan Inner { get; }

  //The placeholder for the keys the projection will produce. The executor projects a set of its
  //own and binds it wherever the plan carries this one.
  public KeySet Keys { get; }

  //What the step became: nearColumn In (keys) for Any, nearColumn NotIn (keys) for None.
  public QueryPredicate Conjunct { get; }

  public override string ToString() {
    var side = Direction == RelationDirection.ToTarget ? "target" : "source";
    return $"{Step.Quantifier} across {Step.RelationName} to its {side} " +
      $"({NearColumn} {Conjunct.Operator} {FarCollection}.{FarColumn} of the records matching: {Inner})";
  }
}
