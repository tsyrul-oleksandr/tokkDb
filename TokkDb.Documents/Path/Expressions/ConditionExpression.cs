namespace TokkDb.Documents.Path.Expressions;

public class ConditionExpression : IExpression {
  public IExpression Parent { get; set; }
  public IExpression Left { get; set; }
  public IExpression Right { get; set; }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    value = Parent.Execute(value, root) ?? value;
    
    return null;
  }
}
