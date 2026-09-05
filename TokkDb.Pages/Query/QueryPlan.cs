using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;

namespace TokkDb.Pages.Query;

//What the planner decided: one access path, and everything the path does not settle left to
//be checked against each record it hands back.
//
//The two halves together are the predicate that came in. The access path may return more
//records than the query wants and never fewer, and Filters plus Residual remove the excess —
//so a plan is correct whatever path was chosen, and choosing badly costs time rather than
//answers.
public sealed record QueryPlan(
  string CollectionName,
  AccessPath Path,
  IReadOnlyList<QueryPredicate> Filters,
  IExpression Residual) {

  //The columns a record has to be read for. One field per column, rather than the whole
  //document — which is the difference between this and what DbEntities.GetAll does.
  public IEnumerable<string> FilterColumns => Filters.Select(filter => filter.ColumnName).Distinct();

  //A residual is the part of the predicate that is not a conjunct at all: an OR, a NOT, a
  //relation step. It is evaluated through the expression tree, against the record as it lies
  //on the page.
  public bool HasResidual => Residual is not null;

  public override string ToString() {
    var description = Path.Describe();
    if (Filters.Count > 0) {
      description += $", filtering on {string.Join(" AND ", Filters)}";
    }
    if (HasResidual) {
      description += $", residual {Residual.GetType().Name}";
    }
    return description;
  }
}
