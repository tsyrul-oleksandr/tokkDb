using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public class NullDocumentValue : NullValue, IDocumentValue {
  public virtual void WriteValue(TokkValueWriter writer) { }

  public virtual void ReadValue(TokkDocumentValueReader reader) { }
}
