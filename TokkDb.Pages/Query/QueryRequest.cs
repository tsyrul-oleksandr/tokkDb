using System.Collections.Immutable;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;

namespace TokkDb.Pages.Query;

public enum OrderDirection {
  Ascending,
  Descending
}

//OR-1: one column of an order and the direction it runs in. Only a name: whether the collection
//has such a column is a question for the catalogue, and is asked when the request is planned.
public sealed record OrderColumn {
  public OrderColumn(string columnName, OrderDirection direction = OrderDirection.Ascending) {
    ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
    ColumnName = columnName;
    Direction = direction;
  }

  public string ColumnName { get; }
  public OrderDirection Direction { get; }

  public override string ToString() {
    return Direction == OrderDirection.Descending ? $"{ColumnName} desc" : ColumnName;
  }
}

//RL-2: which way a step crosses its relation, named by the side it reaches rather than by a flag.
public enum RelationDirection {
  //From the source collection to the records its column refers to.
  ToTarget,
  //From the target collection to the records that refer to it.
  ToSource
}

//A step across a declared relation, by name. The name is all a request can hold: which
//collections the relation joins, and so which way the step goes and whether it has to say, is
//known only to the catalogue (QM-4a).
public sealed record RelationStep {
  public RelationStep(string relationName, RelationQuantifier quantifier, NormalizedQuery inner = null,
      RelationDirection? direction = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(relationName);
    RelationName = relationName;
    Quantifier = quantifier;
    Inner = QueryRequest.Copy(inner ?? NormalizedQuery.Everything);
    Direction = direction;
  }

  public string RelationName { get; }
  public RelationQuantifier Quantifier { get; }

  //The predicate on the far collection. Everything when the step only asks whether a related
  //record exists at all.
  public NormalizedQuery Inner { get; }

  //Null to have it inferred from the side of the relation the queried collection is on.
  public RelationDirection? Direction { get; }

  public override string ToString() {
    var step = $"{Quantifier} across {RelationName}";
    if (Direction is { } direction) {
      step += $" {direction}";
    }
    return Inner.IsEverything ? step : $"{step} where {Inner}";
  }
}

//Q-11: what was asked, and nothing about how it will be answered.
//
//Immutable to the bottom of every collection it owns (QM-1). The order, the relation steps, the
//id list and the conjuncts with their constants are copied into immutable arrays when the request
//is made, so a list the caller passed in and goes on changing cannot change the request. The
//residual is an expression tree and is held as it was given: it is the document layer's object,
//and NormalizedQuery stays exactly what it is (Q-1).
//
//Validated against nothing but itself (QM-4). Every refusal that needs no catalogue is raised
//here, so a request that exists is one a planner may be handed; the ones that do need a catalogue
//wait for QueryRequestPlanner. There are no setters and no init accessors, so there is no way to
//make a request that did not pass through this constructor.
public sealed record QueryRequest {
  public QueryRequest(NormalizedQuery query = null, IEnumerable<Ulid> ids = null,
      IEnumerable<OrderColumn> order = null, long skip = 0, long? take = null,
      IEnumerable<RelationStep> relationSteps = null) {
    if (skip < 0) {
      throw new QueryRequestRefusedException(QueryRequestRefusal.NegativeSkip,
        $"Skip({skip}) is negative. A query can skip no records or some, not fewer than none.");
    }
    if (take < 0) {
      throw new QueryRequestRefusedException(QueryRequestRefusal.NegativeTake,
        $"Take({take}) is negative. Take(0) is an empty page; there is nothing below it.");
    }
    Order = Copy(order, nameof(order));
    //Q-2: page two of an unordered query is not the records after page one, it is a second
    //arbitrary handful, and paging over that is a wrong answer that looks like a right one.
    if (skip > 0 && Order.IsEmpty) {
      throw new QueryRequestRefusedException(QueryRequestRefusal.SkipWithoutOrder,
        $"Skip({skip}) needs an order (Q-2). Without one the records before the page are whichever the " +
        $"access path happened to yield, so the page after them is not defined. Order the query, or use " +
        $"Take alone for any {take?.ToString() ?? "N"} records.");
    }
    var steps = ImmutableArray.CreateBuilder<RelationStep>();
    Query = Copy(Lift(query ?? NormalizedQuery.Everything, steps));
    steps.AddRange(Copy(relationSteps, nameof(relationSteps)));
    foreach (var step in steps) {
      RequireSupported(step);
    }
    RelationSteps = steps.ToImmutable();
    Ids = ids is null ? null : Distinct(ids);
    Skip = skip;
    Take = take;
  }

  //The predicate on the queried collection, with every relation step lifted out of it.
  public NormalizedQuery Query { get; }

  //Null when the query is not restricted by identity. An empty list is a restriction to no
  //record at all — which is what it says, and what a caller whose list of ids came out empty
  //expects back.
  public ImmutableArray<Ulid>? Ids { get; }

  public ImmutableArray<OrderColumn> Order { get; }

  //QM-4b: both held in 64 bits, so no count a caller can pass wraps into a negative window.
  public long Skip { get; }

  //Null for no limit. Zero is a page of nothing, not "no limit" (QM-5).
  public long? Take { get; }

  //Every relation step, whether it came in the predicate or beside it. All of them narrow the
  //query, because a step is only accepted where it is required on its own (RL-3).
  public ImmutableArray<RelationStep> RelationSteps { get; }

  public bool IsOrdered => !Order.IsEmpty;

  //Where the page ends, exclusive: Skip + Take, saturating at long.MaxValue rather than wrapping,
  //and long.MaxValue when there is no Take. A position is compared against this and nothing is
  //ever sized from it (QM-4b) — the caller chose both numbers.
  public long End => Take is { } take && take <= long.MaxValue - Skip ? Skip + take : long.MaxValue;

  public override string ToString() {
    var parts = new List<string> { Query.ToString() };
    if (Ids is { } ids) {
      parts.Add(ids.Length == 1 ? "by id" : $"by {ids.Length} ids");
    }
    parts.AddRange(RelationSteps.Select(step => step.ToString()));
    if (IsOrdered) {
      parts.Add($"order by {string.Join(", ", Order)}");
    }
    if (Skip > 0) {
      parts.Add($"skip {Skip}");
    }
    if (Take is { } limit) {
      parts.Add($"take {limit}");
    }
    return string.Join("; ", parts);
  }

  //The conjuncts and their constants as immutable arrays. A QueryPredicate is a record, so this is
  //a copy of it; the NormalizedQuery type itself is left as it is.
  internal static NormalizedQuery Copy(NormalizedQuery query) {
    var conjuncts = query.Conjuncts
      .Select(conjunct => conjunct with { Constants = ImmutableArray.CreateRange(conjunct.Constants) })
      .ToImmutableArray();
    return new NormalizedQuery(conjuncts, query.Residual);
  }

  private static ImmutableArray<TItem> Copy<TItem>(IEnumerable<TItem> items, string parameterName)
      where TItem : class {
    var copied = items is null ? [] : ImmutableArray.CreateRange(items);
    if (copied.Any(item => item is null)) {
      throw new ArgumentException("The list holds a null entry.", parameterName);
    }
    return copied;
  }

  //A set: the same record named twice is one record, and would otherwise be read twice.
  private static ImmutableArray<Ulid> Distinct(IEnumerable<Ulid> ids) {
    var seen = new HashSet<Ulid>();
    return ids.Where(seen.Add).ToImmutableArray();
  }

  //RL-3. The operands of a top-level conjunction are each required on their own, so a relation
  //step among them can be taken out of the predicate and carried beside it without changing what
  //the query means. Anywhere else it constrains nothing by itself, and taking it out would return
  //records the caller did not ask for — so it is refused rather than lifted.
  private static NormalizedQuery Lift(NormalizedQuery query, ImmutableArray<RelationStep>.Builder steps) {
    var residual = Lift(query.Residual, steps);
    return ReferenceEquals(residual, query.Residual) ? query : new NormalizedQuery(query.Conjuncts, residual);
  }

  private static IExpression Lift(IExpression expression, ImmutableArray<RelationStep>.Builder steps) {
    switch (expression) {
      case null:
        return null;
      case RelationStepExpression step:
        steps.Add(new RelationStep(step.RelationName, step.Quantifier, QueryNormalizer.Normalize(step.Inner)));
        return null;
      case AndExpression and: {
        var kept = new List<IExpression>(and.Operands.Count);
        var changed = false;
        foreach (var operand in and.Operands) {
          var remaining = Lift(operand, steps);
          changed |= !ReferenceEquals(remaining, operand);
          if (remaining is not null) {
            kept.Add(remaining);
          }
        }
        if (!changed) {
          return and;
        }
        return kept.Count switch {
          0 => null,
          1 => kept[0],
          _ => new AndExpression(kept)
        };
      }
      default:
        if (FindStep(expression, position: null) is { } buried) {
          throw new QueryRequestRefusedException(QueryRequestRefusal.RelationStepOutsideConjunction,
            $"Relation step '{buried.Step.RelationName}' is {buried.Position}. A relation step is accepted only where it " +
            $"is required on its own, in an AND-conjunctive position (RL-3): the step is lifted out of the " +
            $"predicate to be executed, and lifting it out of that position would change what the query means.");
        }
        return expression;
    }
  }

  //RL-7 and RL-8.
  private static void RequireSupported(RelationStep step) {
    if (step.Quantifier == RelationQuantifier.All) {
      throw new NotSupportedException(
        $"Relation step '{step.RelationName}' uses the {nameof(RelationQuantifier.All)} quantifier, which is not " +
        $"supported (Q-6). All quantifies over the far records one near record reaches, and a near column " +
        $"holds a single value, so there is nothing for it to quantify over. Use " +
        $"{nameof(RelationQuantifier.Any)} or {nameof(RelationQuantifier.None)}.");
    }
    if (FindStep(step.Inner.Residual, position: "inside it") is { } nested) {
      throw new QueryRequestRefusedException(QueryRequestRefusal.NestedRelationStep,
        $"Relation step '{step.RelationName}' has relation step '{nested.Step.RelationName}' inside its predicate. " +
        $"Only one hop across relations is supported (RL-8).");
    }
  }

  //The first relation step in an expression, and where it sits: the outermost position that is not
  //a conjunction, because that is what stopped it being lifted.
  private static (RelationStepExpression Step, string Position)? FindStep(IExpression expression,
      string position) {
    switch (expression) {
      case RelationStepExpression step:
        return (step, position);
      case AndExpression and:
        return and.Operands.Select(operand => FindStep(operand, position)).FirstOrDefault(found => found is not null);
      case OrExpression or:
        return or.Operands.Select(operand => FindStep(operand, position ?? "under an Or"))
          .FirstOrDefault(found => found is not null);
      case NotExpression not:
        return FindStep(not.Operand, position ?? "under a Not");
      case ComparisonExpression comparison:
        return FindStep(comparison.Left, position ?? "inside a comparison")
          ?? FindStep(comparison.Right, position ?? "inside a comparison");
      default:
        return null;
    }
  }
}
