using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4, stage two: the conjuncts the access path did not settle, then the residual, against the
//record as it lies on the page. Owns records matched.
//
//The checking is done by the same expression tree the query arrived as (DC-5). Nothing here
//interprets an operator: a BufferedObjectValue is an IFieldSource just as a parsed object is,
//so the comparison that would have run against a document runs against the buffer unchanged.
internal sealed class PredicateEvaluation {
  private readonly IExpression[] _filters;
  private readonly IExpression _residual;

  public PredicateEvaluation(QueryPlan plan) {
    _filters = plan.Filters.Select(ToFilter).ToArray();
    _residual = plan.Residual;
  }

  public int RecordsMatched { get; private set; }

  public bool Satisfies(in Candidate candidate) {
    var value = (IDocumentValue)candidate.Fields;
    foreach (var filter in _filters) {
      if (!BooleanExpression.IsTrue(filter.Execute(value, value))) {
        return false;
      }
    }
    //Last, because it is the expensive half: a conjunct reads one field and a residual may
    //walk a whole subtree of the predicate.
    if (_residual is not null && !BooleanExpression.IsTrue(_residual.Execute(value, value))) {
      return false;
    }
    RecordsMatched++;
    return true;
  }

  //The conjunct as the expression it was lifted out of. Rebuilding it rather than interpreting
  //the predicate keeps one evaluator for the two halves of the query, so a conjunct and a residual
  //comparing the same column can never disagree. A conjunct whose constants are a projected key
  //set is the one exception (RL-3b): it is answered by membership in the set, one probe, rather
  //than by a comparison against each of ten thousand keys in turn.
  private static IExpression ToFilter(QueryPredicate predicate) {
    var column = new PropertyExpression(predicate.ColumnName) { Parent = new RootExpression() };
    if (predicate.Constants is KeySet keys) {
      return new KeyMembershipExpression(column, predicate.Operator, keys);
    }
    return new ComparisonExpression(column, predicate.Operator,
      new ConstantExpression(predicate.Constants), predicate.ColumnType);
  }
}

//RL-5 and RL-10, as one expression: the record's key is in the set, or it is not. The field is
//encoded exactly as the index over its column would encode it, so it meets the projected keys
//on their own terms; a null field, a missing one, or one holding an object or an array has no
//key and is a member of nothing — which satisfies NotIn and not In, with no third value anywhere.
internal sealed class KeyMembershipExpression : IExpression {
  private readonly IExpression _column;
  private readonly ComparisonOperator _operator;
  private readonly KeySet _keys;

  public KeyMembershipExpression(IExpression column, ComparisonOperator op, KeySet keys) {
    if (op is not (ComparisonOperator.In or ComparisonOperator.NotIn)) {
      throw new ArgumentException($"A key set answers In and NotIn, not {op}.", nameof(op));
    }
    _column = column;
    _operator = op;
    _keys = keys;
  }

  public IExpression Parent { get; set; }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    var field = _column.Execute(value, root);
    var member = field is not (null or NullDocumentValue) && TryEncode(field, out var key) && _keys.Contains(key);
    return new BooleanDocumentValue(_operator == ComparisonOperator.In ? member : !member);
  }

  private static bool TryEncode(IDocumentValue field, out byte[] key) {
    try {
      key = KeyEncoder.Encode(field).Bytes;
      return true;
    } catch (NotSupportedException) {
      key = null;
      return false;
    }
  }
}
