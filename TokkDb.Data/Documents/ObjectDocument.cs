using TokkDb.Data.Documents.Buffer;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class ObjectDocument {
  public IDocumentValue Value { get; protected set; }
  public IDocumentValue IdentifierValue { get; protected set; }

  public virtual void Write(TokkValueWriter writer) {
    writer.Write(IdentifierValue);
    writer.Write(Value);
  }
  
  public virtual void Read(TokkValueReader reader) {
    IdentifierValue = reader.Read();
    Value = reader.Read();
  }
  
  public virtual void SetValue(IDocumentValue value) {
    if (value.Type != DocumentValueType.Object) {
      throw new ArgumentException("Value must be object", nameof(value));
    }
    Value = value;
  }
  
  public virtual void SetIdentifierValue(IDocumentValue value) {
    if (value.Type is not (DocumentValueType.Int or DocumentValueType.String or DocumentValueType.UInt 
        or DocumentValueType.Long or DocumentValueType.ULong or DocumentValueType.Guid or DocumentValueType.Ulid)) {
      throw new ArgumentException("Value must be an identifier value", nameof(value));
    }
    IdentifierValue = value;
  }
}
