using TokkDb.Data.Documents.Buffer;
using TokkDb.Data.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Data.Documents;

public class ObjectDocument {
  public IDocumentValue Value { get; protected set; }
  public IDocumentValue IdentifierValue { get; protected set; }

  public virtual void Write(TokkValueWriter writer) {
    writer.Write(IdentifierValue);
    writer.Write(Value);
  }
  
  public virtual void Read(TokkDocumentValueReader reader) {
    IdentifierValue = reader.Read();
    Value = reader.Read();
  }
  
  public virtual void SetValue(IDocumentValue value) {
    if (value.Type != ValueTypeEnum.Object) {
      throw new ArgumentException("Value must be object", nameof(value));
    }
    Value = value;
  }
  
  public virtual void SetIdentifierValue(IDocumentValue value) {
    if (value.Type is not (ValueTypeEnum.Int or ValueTypeEnum.String or ValueTypeEnum.UInt 
        or ValueTypeEnum.Long or ValueTypeEnum.ULong or ValueTypeEnum.Guid or ValueTypeEnum.Ulid)) {
      throw new ArgumentException("Value must be an identifier value", nameof(value));
    }
    IdentifierValue = value;
  }
}
