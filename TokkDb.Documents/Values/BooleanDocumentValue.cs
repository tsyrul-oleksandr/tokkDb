using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class BooleanDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Boolean;
  public bool Value { get; set; }

  public BooleanDocumentValue() { }
  public BooleanDocumentValue(bool value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteByte(Value ? (byte)1 : (byte)0);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadByte() != 0;
  }
}
