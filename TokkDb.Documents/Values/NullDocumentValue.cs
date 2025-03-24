using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class NullDocumentValue : NullValue, IDocumentValue {
  public virtual void WriteValue(BufferWriter writer) { }

  public virtual void ReadValue(BufferReader reader) { }
}
