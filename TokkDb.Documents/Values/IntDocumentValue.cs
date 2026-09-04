using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class IntDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Int;
  public int Value { get; set; }

  public IntDocumentValue() { }
  public IntDocumentValue(int value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteInt(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadInt();
  }
}
