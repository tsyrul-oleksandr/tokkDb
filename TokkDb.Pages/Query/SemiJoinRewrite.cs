using System.Collections.Immutable;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;

namespace TokkDb.Pages.Query;

//RL-3a and Q-5: a relation step as a function on the normalised query.
//
//The step is gone from the residual already — the request lifted it out of the conjunction it
//sat in, or refused it where lifting would have changed the query's meaning (RL-3) — and here it
//becomes one conjunct on the near column: In over the keys the step projects for Any, NotIn over
//them for None (RL-5). The conjunct goes to the planner like any other (RL-9), which is what
//turns a capability the engine did not have into one it already had.
internal static class SemiJoinRewrite {
  public static NormalizedQuery Apply(NormalizedQuery query, ImmutableArray<RelationStepPlan> steps) {
    if (steps.IsEmpty) {
      return query;
    }
    var conjuncts = ImmutableArray.CreateRange([.. query.Conjuncts, .. steps.Select(step => step.Conjunct)]);
    return new NormalizedQuery(conjuncts, query.Residual);
  }
}

//RL-3a's invariant, as a check on every plan rather than a test of one case: no plan reaching
//the executor carries a RelationStepExpression, in a conjunct or anywhere in the residual tree.
//A conjunct is a column, an operator and constants and cannot hold an expression, so the walk is
//over the residual; it is recursive because a step under an And under an Or is still a step.
internal static class PlanInvariants {
  public static void RequireNoRelationStep(QueryPlan plan) {
    if (Find(plan.Residual) is { } step) {
      throw new QueryPlanInvariantException(
        $"The plan for {plan.CollectionName} still carries relation step '{step.RelationName}' in its residual. " +
        $"A relation step is executed as a semi-join and rewritten into a conjunct before a plan reaches the " +
        $"executor (RL-3a); a step left behind would throw on the first record. Build the query through " +
        $"DbEntities.Query() so that the step is lifted and rewritten.");
    }
  }

  private static RelationStepExpression Find(IExpression expression) {
    return expression switch {
      null => null,
      RelationStepExpression step => step,
      AndExpression and => and.Operands.Select(Find).FirstOrDefault(found => found is not null),
      OrExpression or => or.Operands.Select(Find).FirstOrDefault(found => found is not null),
      NotExpression not => Find(not.Operand),
      ComparisonExpression comparison => Find(comparison.Left) ?? Find(comparison.Right),
      _ => null
    };
  }
}

//RL-4: the inner query of a semi-join is not a request. Exactly four things — the predicate on
//the far collection as the planner resolved it, the column to project, the catalogue lease it
//runs under (RL-12), and a cancellation token (NF-4) — and no member for an order, a page or a
//result, so that the waste cannot be expressed rather than merely discouraged.
internal sealed record KeyProjection(
  QueryPlan Predicate,
  string Column,
  CatalogLease Lease,
  CancellationToken Cancellation);
