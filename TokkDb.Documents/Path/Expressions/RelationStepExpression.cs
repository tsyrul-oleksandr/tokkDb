namespace TokkDb.Documents.Path.Expressions;

//A step across a declared relation, with a quantifier over what it reaches.
//
//It has no Execute of its own on purpose. Every other node in the tree answers from the
//document in front of it; this one has to read another collection, which needs the index on
//the target column (DC-4) and therefore the planner. It is in the tree so that a query
//carrying one is still one whole expression, and normalisation puts it in the residual —
//which is exactly what a residual is for.
public class RelationStepExpression : IExpression {
  public IExpression Parent { get; set; }
  public string RelationName { get; }
  public string SourceColumn { get; }
  public string TargetCollection { get; }
  public string TargetColumn { get; }
  public RelationQuantifier Quantifier { get; }
  public IExpression Inner { get; }

  public RelationStepExpression(string relationName, string sourceColumn, string targetCollection,
      string targetColumn, RelationQuantifier quantifier, IExpression inner) {
    RelationName = relationName;
    SourceColumn = sourceColumn;
    TargetCollection = targetCollection;
    TargetColumn = targetColumn;
    Quantifier = quantifier;
    Inner = inner;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    throw new NotSupportedException(
      $"Relation '{RelationName}' reaches into {TargetCollection}, which one document cannot answer. " +
      $"A relation step is executed by the planner, against the index on {TargetCollection}.{TargetColumn}.");
  }
}

public enum RelationQuantifier {
  Any,
  None,
  All
}
