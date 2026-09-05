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
      case ValueTypeEnum.Object when value is ObjectDocumentValue objectValue: {
        return objectValue.Values.GetValueOrDefault(_propertyName);
      }
      case ValueTypeEnum.Array when value is ArrayDocumentValue arrayValue && int.TryParse(_propertyName, out var arrayIndex): {
        return arrayValue.Values.Length <= arrayIndex ? null : arrayValue;
      }
      default:
        return null;
    }
  }
}
