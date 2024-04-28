using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public class StringDocumentValue : StringValue, IDocumentValue {
  
  public virtual void WriteValue(TokkValueWriter writer) {
    writer.WriteString(Value);
  }

  public virtual void ReadValue(TokkDocumentValueReader reader) {
    Value = reader.ReadString();
  }
}
