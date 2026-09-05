using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;

namespace TokkDb.Documents.Path.Normalization;

//DC-5. Turns an expression tree into a conjunction of comparisons plus a residual, which is
//the form the planner needs: each conjunct names a column, an operator and constants, so it
//can be matched against the available indexes without evaluating anything.
//
//The split is exact, not approximate. Only the operands of a top-level AND can be lifted,
//because only there is each one required on its own; anything under an OR or a NOT
//constrains nothing by itself and stays whole in the residual. So conjuncts AND residual is
//the predicate that came in, and a planner that narrows by the conjuncts and re-checks the
//residual returns exactly the right records.
public static class QueryNormalizer {
  public static NormalizedQuery Normalize(IExpression expression) {
    if (expression is null) {
      return NormalizedQuery.Everything;
    }
    var conjuncts = new List<QueryPredicate>();
    var residuals = new List<IExpression>();
    Split(expression, conjuncts, residuals);
    return new NormalizedQuery(conjuncts, Conjoin(residuals));
  }

  private static void Split(IExpression expression, List<QueryPredicate> conjuncts,
      List<IExpression> residuals) {
    //Nested ANDs are one AND. A query built by hand tends to nest them and a query built by
    //a binder always does.
    if (expression is AndExpression and) {
      foreach (var operand in and.Operands) {
        Split(operand, conjuncts, residuals);
      }
      return;
    }
    if (AsPredicate(expression) is { } predicate) {
      conjuncts.Add(predicate);
      return;
    }
    residuals.Add(expression);
  }

  //A comparison fits the shape when one side is a bare column of the collection under filter
  //and the other is constant. A comparison of two columns, or of a path that goes deeper than
  //one property, does not: it names no single column an index could be chosen by.
  private static QueryPredicate AsPredicate(IExpression expression) {
    if (expression is not ComparisonExpression comparison) {
      return null;
    }
    if (ColumnName(comparison.Left) is { } leftColumn && comparison.Right is ConstantExpression constants) {
      return new QueryPredicate(leftColumn, comparison.Operator, comparison.ColumnType, constants.Values);
    }
    //Written the other way round — 40 <= Price — is the same predicate with the operator
    //turned about.
    if (ColumnName(comparison.Right) is { } rightColumn && comparison.Left is ConstantExpression reversed) {
      return new QueryPredicate(rightColumn, comparison.Operator.Flip(), comparison.ColumnType,
        reversed.Values);
    }
    return null;
  }

  //One property read from the root of the record, and nothing else. "$.Age" is a column;
  //"$.Passport.Code" is a path into a document and names no column of the collection.
  private static string ColumnName(IExpression expression) {
    return expression is PropertyExpression property && property.Parent is null or RootExpression
      ? property.PropertyName
      : null;
  }

  private static IExpression Conjoin(List<IExpression> residuals) {
    return residuals.Count switch {
      0 => null,
      1 => residuals[0],
      _ => new AndExpression(residuals)
    };
  }
}
