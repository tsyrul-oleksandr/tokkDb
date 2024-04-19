using TokkDb.Core.Address;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class TokkDocument {
  public BufferAddress Address { get; set; }
  public DocumentIdentifierType IdentifierType { get; set; }
  public BaseValue Value { get; set; }
  public BaseValue IdentifierValue { get; set; }

  public void Write(TokkValueWriter writer) {
    writer.Write(IdentifierValue);
    writer.Write(Value);
  }
  
  public void Read(TokkValueReader reader) {
    IdentifierValue = reader.Read();
    Value = reader.Read();
  }
}
