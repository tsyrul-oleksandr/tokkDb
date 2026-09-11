using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//Sixteen bytes big endian — the same layout KeyEncoder writes a Guid key in, so the stored
//bytes and the key bytes are the same bytes in the same order.
public class GuidDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Guid;
  public Guid Value { get; set; }

  public GuidDocumentValue() { }
  public GuidDocumentValue(Guid value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteGuid(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadGuid();
  }
}
