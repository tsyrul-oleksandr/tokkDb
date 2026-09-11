using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class LongDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Long;
  public long Value { get; set; }

  public LongDocumentValue() { }
  public LongDocumentValue(long value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteLong(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadLong();
  }
}
