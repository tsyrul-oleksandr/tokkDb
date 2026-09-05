using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Path.Expressions;

public class PropertyExpression : IExpression {
  private readonly string _propertyName;
  public IExpression Parent { get; set; }

  //Normalisation has to be able to tell a bare column from a path into a document.
  public string PropertyName => _propertyName;

  public PropertyExpression(string propertyName) {
    _propertyName = propertyName;
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    value = Parent?.Execute(value, root) ?? value;
    switch (value.Type) {
      //Any field source, not only a parsed object: Phase 6 hands the predicate the record as
      //it lies on the page, and it has to answer here exactly as a parsed one would.
      case ValueTypeEnum.Object when value is IFieldSource source: {
        return source.GetField(_propertyName);
      }
      case ValueTypeEnum.Array when value is ArrayDocumentValue arrayValue && int.TryParse(_propertyName, out var arrayIndex): {
        return arrayValue.Values.Length <= arrayIndex ? null : arrayValue;
      }
      default:
        return null;
    }
  }
}
