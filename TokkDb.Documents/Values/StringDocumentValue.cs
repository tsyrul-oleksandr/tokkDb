using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class StringDocumentValue : StringValue, IDocumentValue {
  
  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteString(Value);
  }

  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadString();
  }
}
