using TokkDb.Documents.Values;

namespace TokkDb.Documents.Path.Expressions;

//The boolean structure of a query. Kept separate from the path expressions, which navigate
//to a value: these take truth to truth.
public class AndExpression : IExpression {
  public IExpression Parent { get; set; }
  public IReadOnlyList<IExpression> Operands { get; }

  public AndExpression(IReadOnlyList<IExpression> operands) {
    Operands = operands;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    foreach (var operand in Operands) {
      if (!BooleanExpression.IsTrue(operand.Execute(value, root))) {
        return new BooleanDocumentValue(false);
      }
    }
    return new BooleanDocumentValue(true);
  }
}

public class OrExpression : IExpression {
  public IExpression Parent { get; set; }
  public IReadOnlyList<IExpression> Operands { get; }

  public OrExpression(IReadOnlyList<IExpression> operands) {
    Operands = operands;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    foreach (var operand in Operands) {
      if (BooleanExpression.IsTrue(operand.Execute(value, root))) {
        return new BooleanDocumentValue(true);
      }
    }
    return new BooleanDocumentValue(false);
  }
}

public class NotExpression : IExpression {
  public IExpression Parent { get; set; }
  public IExpression Operand { get; }

  public NotExpression(IExpression operand) {
    Operand = operand;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    return new BooleanDocumentValue(!BooleanExpression.IsTrue(Operand.Execute(value, root)));
  }
}

public static class BooleanExpression {
  //A path expression answers with the value it found or null, and a comparison answers with
  //a boolean. Both have to mean something here, so "found" is true and "not found" is false.
  public static bool IsTrue(IDocumentValue value) {
    return value switch {
      null or NullDocumentValue => false,
      BooleanDocumentValue flag => flag.Value,
      _ => true
    };
  }
}
