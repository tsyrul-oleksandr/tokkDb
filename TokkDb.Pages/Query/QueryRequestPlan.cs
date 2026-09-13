using System.Collections.Immutable;

namespace TokkDb.Pages.Query;

//Q-11: a request resolved against one version of the catalogue.
//
//A concrete value rather than a promise to plan (QM-2). Explain hands one back, and Run executes
//it as it stands; the version it carries is what Run checks it against, under the lease, before
//it reads anything (QM-2a). Only the planner makes one, so a plan that exists was checked against
//the catalogue it names.
public sealed record QueryRequestPlan {
  internal QueryRequestPlan(string collectionName, QueryRequest request, CatalogLease lease, QueryPlan access,
      ImmutableArray<RelationStepPlan> relationSteps) {
    CollectionName = collectionName;
    Request = request;
    CatalogVersion = lease.Version;
    Catalog = lease.Owner;
    Access = access;
    RelationSteps = relationSteps;
  }

  public string CollectionName { get; }

  //What was asked. Kept so that a caller whose plan went stale can plan it again in one call.
  public QueryRequest Request { get; }

  public long CatalogVersion { get; }

  //How the queried collection is reached, and what is re-checked against each record it yields.
  public QueryPlan Access { get; }

  public ImmutableArray<RelationStepPlan> RelationSteps { get; }

  public ImmutableArray<OrderColumn> Order => Request.Order;
  public long Skip => Request.Skip;
  public long? Take => Request.Take;

  internal CatalogLock Catalog { get; }

  public override string ToString() {
    var description = Access.ToString();
    foreach (var step in RelationSteps) {
      description += $", {step}";
    }
    if (Request.IsOrdered) {
      description += $", ordered by {string.Join(", ", Order)}";
    }
    if (Skip > 0) {
      description += $", skipping {Skip}";
    }
    if (Take is { } take) {
      description += $", taking {take}";
    }
    return $"{description} (catalogue version {CatalogVersion})";
  }
}

//RL-1 and RL-2: a relation step with its relation resolved — which side of it the queried
//collection is on, and so which of its columns is narrowed and which collection is read to do it.
public sealed record RelationStepPlan {
  internal RelationStepPlan(RelationStep step, RelationDirection direction, string nearColumn,
      string farCollection, string farColumn, QueryPlan inner) {
    Step = step;
    Direction = direction;
    NearColumn = nearColumn;
    FarCollection = farCollection;
    FarColumn = farColumn;
    Inner = inner;
  }

  public RelationStep Step { get; }

  //Stated or inferred; resolved either way.
  public RelationDirection Direction { get; }

  //The column of the queried collection the step narrows.
  public string NearColumn { get; }

  public string FarCollection { get; }
  public string FarColumn { get; }

  //How the far collection is reached for the step's predicate, planned under the same lease as
  //the query it narrows (RL-12).
  public QueryPlan Inner { get; }

  public override string ToString() {
    var side = Direction == RelationDirection.ToTarget ? "target" : "source";
    return $"{Step.Quantifier} across {Step.RelationName} to its {side} " +
      $"({NearColumn} in {FarCollection}.{FarColumn}, inner: {Inner})";
  }
}
