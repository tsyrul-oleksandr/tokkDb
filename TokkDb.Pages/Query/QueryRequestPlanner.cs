using System.Collections.Immutable;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Values;

namespace TokkDb.Pages.Query;

//Q-11: turns a request into a plan against the catalogue as the lease holds it.
//
//The refusals here are the ones a request cannot raise about itself (QM-4a): a column, a relation
//and the side of a relation are facts about the catalogue, and this is the first moment there is
//one to check them against. Explain and Run both come through here, which is what makes them
//refuse identically.
//
//Planning reads the catalogue and nothing else. Every descriptor it consults is held in memory
//from open, so a plan costs no data page however large the collection is.
public static class QueryRequestPlanner {
  public static QueryRequestPlan Plan(string collectionName, QueryRequest request, CatalogLease lease,
      CollectionCatalog catalog, IndexCatalog indexes, RelationCatalog relations) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(lease);
    var collection = catalog.Get(collectionName);
    foreach (var column in request.Order) {
      RequireOrderable(collection, column);
    }
    var steps = request.RelationSteps
      .Select(step => Resolve(collection, step, indexes, relations))
      .ToImmutableArray();
    return new QueryRequestPlan(collectionName, request, lease, Access(collectionName, request, indexes), steps);
  }

  //The access path is chosen by the same rule the existing entry point uses (DC-5), so the two
  //cannot disagree about how a predicate is reached.
  private static QueryPlan Access(string collectionName, QueryRequest request, IndexCatalog indexes) {
    //An empty id list restricts the query to nothing. QueryPlanner reads an empty list as no
    //restriction, which is what its own entry point has always meant, so the empty set is
    //answered here: a lookup of no ids reads nothing, and says so.
    if (request.Ids is { IsEmpty: true }) {
      return new QueryPlan(collectionName, new PrimaryKeyPath(collectionName, ImmutableArray<Ulid>.Empty),
        request.Query.Conjuncts, request.Query.Residual);
    }
    IReadOnlyList<Ulid> ids = request.Ids is { } named ? named : null;
    return QueryPlanner.Plan(collectionName, request.Query, ids, indexes);
  }

  //N-2. A collection that declares no columns accepts any name, as an index or a relation over it
  //does.
  private static void RequireOrderable(CollectionDescriptor collection, OrderColumn order) {
    if (collection.Columns.Count == 0) {
      return;
    }
    var column = collection.Columns.FirstOrDefault(candidate => candidate.Name == order.ColumnName);
    if (column is null) {
      throw new QueryPlanRefusedException(QueryPlanRefusal.UnknownOrderColumn,
        $"The order names column '{order.ColumnName}', which collection '{collection.Name}' does not have. " +
        $"It declares {string.Join(", ", collection.Columns.Select(candidate => candidate.Name))}.");
    }
    //The same reason an index over such a column is refused: an object and an array have no
    //ordering, so there is no key to put the records in order by.
    if (column.Type is ValueTypeEnum.Object or ValueTypeEnum.Array) {
      throw new QueryPlanRefusedException(QueryPlanRefusal.UnorderableOrderColumn,
        $"The order names column '{order.ColumnName}' of collection '{collection.Name}', which holds " +
        $"{column.Type} values. They have no ordering; order by a scalar column instead.");
    }
  }

  //N-3 and N-7.
  private static RelationStepPlan Resolve(CollectionDescriptor collection, RelationStep step,
      IndexCatalog indexes, RelationCatalog relations) {
    var relation = relations.Descriptors.FirstOrDefault(candidate => candidate.Name == step.RelationName)
      ?? throw new QueryPlanRefusedException(QueryPlanRefusal.UnknownRelation,
        $"The query steps across relation '{step.RelationName}', which is not declared. {Declared(relations)}");
    var direction = Direction(collection.Name, relation, step);
    var (nearColumn, farCollection, farColumn) = direction == RelationDirection.ToTarget
      ? (relation.SourceColumn, relation.TargetCollection, relation.TargetColumn)
      : (relation.TargetColumn, relation.SourceCollection, relation.SourceColumn);
    var inner = QueryPlanner.Plan(farCollection, step.Inner, null, indexes);
    return new RelationStepPlan(step, direction, nearColumn, farCollection, farColumn, inner);
  }

  //RL-2: inferred from the side the queried collection is on, and required only when it is on
  //both, because only then does the name leave the direction open.
  private static RelationDirection Direction(string collectionName, RelationDescriptor relation, RelationStep step) {
    var isSource = relation.SourceCollection == collectionName;
    var isTarget = relation.TargetCollection == collectionName;
    var declared = $"{relation.SourceCollection}.{relation.SourceColumn} to " +
      $"{relation.TargetCollection}.{relation.TargetColumn}";
    if (isSource && isTarget) {
      return step.Direction ?? throw new QueryPlanRefusedException(QueryPlanRefusal.SelfRelationWithoutDirection,
        $"Relation '{relation.Name}' joins collection '{collectionName}' to itself ({declared}), so a step " +
        $"across it has to say which way it goes: {nameof(RelationDirection.ToTarget)} reaches the records " +
        $"whose {relation.TargetColumn} a record's {relation.SourceColumn} holds, " +
        $"{nameof(RelationDirection.ToSource)} the records whose {relation.SourceColumn} holds a record's " +
        $"{relation.TargetColumn}.");
    }
    if (!isSource && !isTarget) {
      throw new QueryPlanRefusedException(QueryPlanRefusal.RelationDirectionMismatch,
        $"Relation '{relation.Name}' joins {declared}, and a query over '{collectionName}' is on neither side of it.");
    }
    var inferred = isSource ? RelationDirection.ToTarget : RelationDirection.ToSource;
    if (step.Direction is { } stated && stated != inferred) {
      throw new QueryPlanRefusedException(QueryPlanRefusal.RelationDirectionMismatch,
        $"Relation '{relation.Name}' joins {declared}, so a query over '{collectionName}' steps across it " +
        $"{inferred}, not {stated}.");
    }
    return inferred;
  }

  private static string Declared(RelationCatalog relations) {
    var names = relations.Descriptors.Select(relation => relation.Name).Order(StringComparer.Ordinal).ToList();
    return names.Count == 0
      ? "No relation is declared."
      : $"Declared: {string.Join(", ", names)}.";
  }
}
