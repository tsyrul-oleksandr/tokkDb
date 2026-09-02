using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class UlidDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Ulid;
  public Ulid Value { get; set; }

  public UlidDocumentValue() { }
  public UlidDocumentValue(Ulid value) {
    Value = value;
  }
  
  public void WriteValue(BufferWriter writer) {
    writer.WriteBytes(Value.ToByteArray());
  }
  public void ReadValue(BufferReader reader) {
    Value = new Ulid(reader.ReadBytes(TypesConstants.UlidByteSize));
  }
}
