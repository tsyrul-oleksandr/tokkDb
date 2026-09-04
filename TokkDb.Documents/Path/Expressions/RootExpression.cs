namespace TokkDb.Documents.Path.Expressions;

public class RootExpression : IExpression {

  public IExpression Parent { get; set; }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    return Parent?.Execute(root, root);
  }
}
