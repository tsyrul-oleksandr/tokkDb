namespace TokkDb.Documents.Path.Expressions;

public class ContextExpression : IExpression {

  public IExpression Parent { get; set; }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    return Parent?.Execute(value, root) ?? value;
  }
}
