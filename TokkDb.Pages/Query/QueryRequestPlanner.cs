using System.Collections.Immutable;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
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
//from open, so a plan costs no data page however large the collection is — and no index page
//either, which is why the height of a tree is not consulted here (RL-3b is decided at execution).
public static class QueryRequestPlanner {
  public static QueryRequestPlan Plan(string collectionName, QueryRequest request, CatalogLease lease,
      CollectionCatalog catalog, IndexCatalog indexes, RelationCatalog relations, QueryOptions options = null) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(lease);
    options ??= QueryOptions.Default;
    var collection = catalog.Get(collectionName);
    foreach (var column in request.Order) {
      RequireOrderable(collection, column);
    }
    var steps = request.RelationSteps
      .Select(step => Resolve(collection, step, catalog, indexes, relations))
      .ToImmutableArray();
    //RL-3a: the rewrite, as a function on the normalised query. The steps are out of the residual
    //already (the request lifted them, or refused them); each goes in as a conjunct beside the
    //others, and the planner sees an In like any other In (RL-9).
    var rewritten = SemiJoinRewrite.Apply(request.Query, steps);
    var predicatePath = Access(collectionName, request, rewritten, indexes);
    var (access, pathReason, orderSource, orderReason) =
      ChooseOrder(collectionName, request, rewritten, predicatePath, indexes, options);
    PlanInvariants.RequireNoRelationStep(access);
    return new QueryRequestPlan(collectionName, request, lease, options, rewritten, access, pathReason, orderSource,
      orderReason, steps);
  }

  //The access path is chosen by the same rule the existing entry point uses (DC-5), so the two
  //cannot disagree about how a predicate is reached.
  private static QueryPlan Access(string collectionName, QueryRequest request, NormalizedQuery query,
      IndexCatalog indexes) {
    //An empty id list restricts the query to nothing. QueryPlanner reads an empty list as no
    //restriction, which is what its own entry point has always meant, so the empty set is
    //answered here: a lookup of no ids reads nothing, and says so.
    if (request.Ids is { IsEmpty: true }) {
      return new QueryPlan(collectionName, new PrimaryKeyPath(collectionName, ImmutableArray<Ulid>.Empty),
        query.Conjuncts, query.Residual);
    }
    IReadOnlyList<Ulid> ids = request.Ids is { } named ? named : null;
    return QueryPlanner.Plan(collectionName, query, ids, indexes);
  }

  //OR-2 and OR-2a. Where the predicate's own path walks an index whose key order the request is a
  //prefix of, the walk is the order and nothing is retained. Otherwise there may be a second path
  //— a walk of an index chosen for the order, with the predicate as a filter — and the stated
  //rule decides between the two: the predicate's path, ordered afterwards, unless the query is
  //bounded by Skip + Take and that path is not selective, in which case walking in order can
  //return the page without examining the whole collection. A rule rather than a cost model,
  //because nothing collects statistics; where it is wrong it is wrong in the plan's own line.
  private static (QueryPlan Access, string PathReason, OrderSource Source, string OrderReason) ChooseOrder(
      string collectionName, QueryRequest request, NormalizedQuery query, QueryPlan predicatePath,
      IndexCatalog indexes, QueryOptions options) {
    var path = predicatePath.Path;
    if (!request.IsOrdered) {
      return (predicatePath, "the predicate's path; no order was asked for", OrderSource.None, "no order was asked for");
    }
    var order = request.Order;
    if (OrderRules.WalkSatisfies(path, order)) {
      return (predicatePath, "the predicate's path, whose walk also yields the order", OrderSource.IndexWalk,
        $"the order is the key order of the walk: {path.Describe()} yields ({OrderRules.DescribeKeyOrder(path)}) " +
        $"and the requested order is a prefix of it (OR-2)");
    }
    var stage = request.Take is null ? OrderSource.CompleteSort : OrderSource.BoundedHeap;
    var stageWork = stage == OrderSource.BoundedHeap
      ? "the ordering stage keeps at most Skip + Take records by the order and discards the rest as they arrive (OR-3a)"
      : "the ordering stage holds every match, because with no Take nothing can be discarded (OR-3b)";
    var noWalk = OrderRules.WhyNoWalkSatisfies(collectionName, order, indexes, path);
    //A list of ids is answered by the primary index and by nothing else; a walk cannot apply it.
    var walk = request.Ids is null ? OrderRules.OrderedWalkFor(collectionName, order, indexes) : null;
    if (walk is null) {
      return (predicatePath, $"the predicate's path, ordered afterwards: {noWalk}", stage, $"{noWalk}; {stageWork}");
    }
    var bounded = request.Take is not null;
    var selective = OrderRules.IsSelective(path);
    var chooseWalk = options.PathChoice switch {
      PathChoice.OrderedWalk => true,
      PathChoice.PredicatePath => false,
      _ => bounded && !selective
    };
    if (chooseWalk) {
      var access = new QueryPlan(collectionName, walk, QueryPlanner.Filters(walk, query.Conjuncts), query.Residual);
      var reason = options.PathChoice == PathChoice.OrderedWalk
        ? $"the ordered walk of the index on {walk.ColumnName}, forced by {nameof(QueryOptions.PathChoice)}, with the " +
          $"predicate applied as a filter over it"
        : $"the ordered walk of the index on {walk.ColumnName}, filtering the predicate (OR-2a): the query is bounded " +
          $"by Skip + Take and the predicate's own path, {path.Describe()}, is not selective, so walking in order " +
          $"can return the page without examining the whole collection";
      return (access, reason, OrderSource.IndexWalk,
        $"the order is the key order of the walk of the index on {walk.ColumnName}, chosen for the order (OR-2a)");
    }
    var kept = options.PathChoice == PathChoice.PredicatePath
      ? $"the predicate's path, forced by {nameof(QueryOptions.PathChoice)}; the ordering stage orders its records"
      : !bounded
        ? $"the predicate's path, ordered afterwards (OR-2a): the query has no Take, so a walk of the index on " +
          $"{walk.ColumnName} in order could not stop at a page and would cost the whole index"
        : $"the predicate's path, ordered afterwards (OR-2a): {path.Describe()} is selective, so its records are " +
          $"ordered by the stage rather than walking the index on {walk.ColumnName} in order and filtering";
    return (predicatePath, kept, stage, $"{noWalk}; {stageWork}");
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

  //N-3 and N-7, and the conjunct the step becomes (RL-3a, RL-5).
  private static RelationStepPlan Resolve(CollectionDescriptor collection, RelationStep step,
      CollectionCatalog catalog, IndexCatalog indexes, RelationCatalog relations) {
    var relation = relations.Descriptors.FirstOrDefault(candidate => candidate.Name == step.RelationName)
      ?? throw new QueryPlanRefusedException(QueryPlanRefusal.UnknownRelation,
        $"The query steps across relation '{step.RelationName}', which is not declared. {Declared(relations)}");
    var direction = Direction(collection.Name, relation, step);
    var (nearColumn, farCollection, farColumn) = direction == RelationDirection.ToTarget
      ? (relation.SourceColumn, relation.TargetCollection, relation.TargetColumn)
      : (relation.TargetColumn, relation.SourceCollection, relation.SourceColumn);
    var inner = QueryPlanner.Plan(farCollection, step.Inner, null, indexes);
    PlanInvariants.RequireNoRelationStep(inner);
    //The near column's declared type, or the far column's where the near collection declares no
    //columns. The keys themselves are compared as keys; the type is what the conjunct reads as.
    var nearType = ColumnType(collection, nearColumn)
      ?? (catalog.Exists(farCollection) ? ColumnType(catalog.Get(farCollection), farColumn) : null)
      ?? ValueTypeEnum.Null;
    var keys = KeySet.Deferred(step.RelationName);
    var conjunct = new QueryPredicate(nearColumn,
      step.Quantifier == RelationQuantifier.Any ? ComparisonOperator.In : ComparisonOperator.NotIn, nearType, keys);
    return new RelationStepPlan(step, direction, nearColumn, nearType, farCollection, farColumn, inner, keys, conjunct);
  }

  private static ValueTypeEnum? ColumnType(CollectionDescriptor collection, string columnName) {
    return collection.Columns.FirstOrDefault(column => column.Name == columnName)?.Type;
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

//OR-2 and OR-2a as rules a test can hold the planner to, written out rather than assumed.
public static class OrderRules {
  //The key order a walk of the path yields, as the columns of the key with the identity
  //understood to follow them ascending (D-3's composite key) — or null where the path has no key
  //order: a scan, a membership pass, or a lookup by a list of ids, which yields the ids' order.
  //
  //A seek is a walk too: the executor visits its values in key order, one descent each, so its
  //records come off in the order of the key's column and then the identity.
  public static IReadOnlyList<OrderColumn> KeyOrderOf(AccessPath path) {
    return path switch {
      IndexRangePath range => [new OrderColumn(range.ColumnName)],
      OrderedIndexWalkPath walk => [new OrderColumn(walk.ColumnName)],
      IndexSeekPath seek => [new OrderColumn(seek.ColumnName)],
      _ => null
    };
  }

  //OR-2: the walk satisfies the request when the request, with the identity tiebreaker of OR-1
  //appended in the direction of its last column, is a prefix of the key order with its identity
  //appended ascending. The key must refine the request, not the other way about: a key of
  //(date, identity) satisfies a request for (date, identity) and does not satisfy one for
  //(date, cost, identity), because records sharing a date come off the walk in identity order
  //and the request wants them in cost order. A descending request is never satisfied, because
  //the leaf chain runs forward only (Q-3).
  //
  //What it reduces to today: every index key is one column and the identity, so the rule admits
  //exactly a single-column ascending order on the walked column. Written as the rule anyway,
  //because the rule is already right for a composite key.
  public static bool WalkSatisfies(AccessPath path, ImmutableArray<OrderColumn> order) {
    if (order.IsDefaultOrEmpty || KeyOrderOf(path) is not { } key) {
      return false;
    }
    //The request names more columns than the key has: where the key continues with the
    //identity, the request wants another column.
    if (order.Length > key.Count) {
      return false;
    }
    for (var i = 0; i < order.Length; i++) {
      if (order[i].ColumnName != key[i].ColumnName || order[i].Direction != key[i].Direction) {
        return false;
      }
    }
    //The request ends and appends the identity. Where the key still has columns, its records
    //are not in identity order there; where it has the identity too, the directions agree
    //because every matched column was ascending.
    return order.Length == key.Count;
  }

  public static string DescribeKeyOrder(AccessPath path) {
    return KeyOrderOf(path) is { } key
      ? $"{string.Join(", ", key.Select(column => column.ColumnName))}, identity"
      : "no key order";
  }

  //OR-2a: whether the path names the records it wants — an identity lookup or a seek — rather
  //than reading a stretch or everything. Without statistics this is the rule's whole notion of
  //selectivity, and it is stated so that where it is wrong it is wrong visibly.
  public static bool IsSelective(AccessPath path) {
    return path is PrimaryKeyPath or IndexSeekPath;
  }

  //The walk that would yield the order, where an index exists whose key order the request is a
  //prefix of.
  public static OrderedIndexWalkPath OrderedWalkFor(string collectionName, ImmutableArray<OrderColumn> order,
      IndexCatalog indexes) {
    foreach (var index in indexes?.For(collectionName) ?? []) {
      var walk = new OrderedIndexWalkPath(collectionName, index.Descriptor.ColumnName);
      if (WalkSatisfies(walk, order)) {
        return walk;
      }
    }
    return null;
  }

  //Why the predicate's path does not yield the order, in the terms a reader can act on.
  public static string WhyNoWalkSatisfies(string collectionName, ImmutableArray<OrderColumn> order,
      IndexCatalog indexes, AccessPath predicatePath) {
    var first = order[0];
    if (indexes?.Find(collectionName, first.ColumnName) is null) {
      return $"no index on {first.ColumnName}, so no walk yields the order";
    }
    if (first.Direction == OrderDirection.Descending) {
      return $"descending is a sort (Q-3): the index on {first.ColumnName} is walked forward only, from leaf to " +
        $"leaf along NextPageIndex, and there is no backward chain to walk (R-1)";
    }
    if (order.Length > 1) {
      return $"the requested order ({string.Join(", ", order)}) is not a prefix of the key order of the index on " +
        $"{first.ColumnName}, which is ({first.ColumnName}, identity): records equal on {first.ColumnName} come off " +
        $"a walk in identity order and not in {order[1].ColumnName} order (OR-2)";
    }
    return $"{predicatePath.Describe()} does not walk the index on {first.ColumnName}";
  }
}
