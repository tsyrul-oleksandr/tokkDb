using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public class IntDocumentValue : IntValue, IDocumentValue {

  public virtual void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Value);
  }

  public virtual void ReadValue(TokkDocumentValueReader reader) {
    Value = reader.ReadInt();
  }
}
