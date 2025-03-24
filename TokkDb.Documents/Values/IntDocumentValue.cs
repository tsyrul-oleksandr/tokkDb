using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class IntDocumentValue : IntValue, IDocumentValue {

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteInt(Value);
  }

  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadInt();
  }
}
