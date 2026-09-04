using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class NullDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Null;
  
  public virtual void WriteValue(BufferWriter writer) { }

  public virtual void ReadValue(BufferReader reader) { }
}
