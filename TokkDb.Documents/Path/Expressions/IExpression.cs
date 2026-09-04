namespace TokkDb.Documents.Path.Expressions;

public interface IExpression {
  IExpression Parent { get; set; }
  IDocumentValue Execute(IDocumentValue value, IDocumentValue root);
}
