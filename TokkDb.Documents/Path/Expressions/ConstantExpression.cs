namespace TokkDb.Documents.Path.Expressions;

//A value written into the query rather than read out of the record. One operand for most
//comparisons, several for In.
public class ConstantExpression : IExpression {
  public IExpression Parent { get; set; }
  public IReadOnlyList<IDocumentValue> Values { get; }

  public ConstantExpression(IDocumentValue value) : this([value]) { }

  public ConstantExpression(IReadOnlyList<IDocumentValue> values) {
    Values = values;
  }

  public IDocumentValue Value => Values[0];

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    return Values[0];
  }
}
