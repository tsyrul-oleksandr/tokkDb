using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Path.Expressions;

public enum ComparisonOperator {
  Equal,
  NotEqual,
  Less,
  LessOrEqual,
  Greater,
  GreaterOrEqual,
  StartsWith,
  EndsWith,
  Contains,
  //One operand list rather than an OR of equalities, because a set of equalities is a shape
  //an index can answer and an OR is not.
  In
}

public static class ComparisonOperators {
  //The ones whose meaning is a position in the ordering, and which an index range can
  //therefore answer. The rest are equality or text matching.
  public static bool IsOrdered(this ComparisonOperator op) {
    return op is ComparisonOperator.Less or ComparisonOperator.LessOrEqual
      or ComparisonOperator.Greater or ComparisonOperator.GreaterOrEqual;
  }

  public static ComparisonOperator Flip(this ComparisonOperator op) {
    return op switch {
      ComparisonOperator.Less => ComparisonOperator.Greater,
      ComparisonOperator.LessOrEqual => ComparisonOperator.GreaterOrEqual,
      ComparisonOperator.Greater => ComparisonOperator.Less,
      ComparisonOperator.GreaterOrEqual => ComparisonOperator.LessOrEqual,
      _ => op
    };
  }
}

//One comparison in the engine's internal form: something on the left, an operator, and
//something on the right. The column type travels with it because the stored form of a value
//is not always the type the column declares (see TypedKey), and a comparison that read the
//stored form alone would order decimals as text.
public class ComparisonExpression : IExpression {
  public IExpression Parent { get; set; }
  public IExpression Left { get; }
  public IExpression Right { get; }
  public ComparisonOperator Operator { get; }
  public ValueTypeEnum ColumnType { get; }

  public ComparisonExpression(IExpression left, ComparisonOperator op, IExpression right,
      ValueTypeEnum columnType) {
    Left = left;
    Operator = op;
    Right = right;
    ColumnType = columnType;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    var left = Left.Execute(value, root);
    return new BooleanDocumentValue(Operator switch {
      ComparisonOperator.In => Matches(left, ((ConstantExpression)Right).Values),
      ComparisonOperator.StartsWith or ComparisonOperator.EndsWith or ComparisonOperator.Contains =>
        MatchesText(left, Right.Execute(value, root)),
      _ => Compare(left, Right.Execute(value, root)) is { } sign && Satisfies(sign)
    });
  }

  private bool Matches(IDocumentValue left, IReadOnlyList<IDocumentValue> candidates) {
    foreach (var candidate in candidates) {
      if (Compare(left, candidate) == 0) {
        return true;
      }
    }
    return false;
  }

  //Over the folded form (D-3), so that a text predicate and the index that could answer it
  //agree about which strings match — an index holds the folded key and nothing else.
  private bool MatchesText(IDocumentValue left, IDocumentValue right) {
    if (left is not StringDocumentValue text || right is not StringDocumentValue pattern) {
      return false;
    }
    var subject = KeyNormalization.Normalize(text.Value);
    var wanted = KeyNormalization.Normalize(pattern.Value);
    return Operator switch {
      ComparisonOperator.StartsWith => subject.StartsWith(wanted, StringComparison.Ordinal),
      ComparisonOperator.EndsWith => subject.EndsWith(wanted, StringComparison.Ordinal),
      ComparisonOperator.Contains => subject.Contains(wanted, StringComparison.Ordinal),
      _ => false
    };
  }

  private int? Compare(IDocumentValue left, IDocumentValue right) {
    var leftKey = TypedKey.Encode(ColumnType, left);
    var rightKey = TypedKey.Encode(ColumnType, right);
    //A value that cannot be read as the type its column declares satisfies no comparison. It
    //is not an error: a record whose field was written wrong is invisible to a query over it.
    return leftKey is null || rightKey is null
      ? null
      : KeyComparer.Compare(leftKey.Value.Bytes, rightKey.Value.Bytes);
  }

  private bool Satisfies(int sign) {
    return Operator switch {
      ComparisonOperator.Equal => sign == 0,
      ComparisonOperator.NotEqual => sign != 0,
      ComparisonOperator.Less => sign < 0,
      ComparisonOperator.LessOrEqual => sign <= 0,
      ComparisonOperator.Greater => sign > 0,
      ComparisonOperator.GreaterOrEqual => sign >= 0,
      _ => false
    };
  }
}
