using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class UIntDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.UInt;
  public uint Value { get; set; }

  public UIntDocumentValue() { }
  public UIntDocumentValue(uint value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteUInt(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadUInt();
  }
}
