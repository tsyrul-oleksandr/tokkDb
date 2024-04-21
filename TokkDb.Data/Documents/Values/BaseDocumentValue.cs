using TokkDb.Data.Documents.Buffer;

namespace TokkDb.Data.Documents.Values;

public abstract class BaseDocumentValue : IDocumentValue {
  public abstract DocumentValueType Type { get; }
  public abstract void WriteValue(TokkValueWriter writer);
  public abstract void ReadValue(TokkValueReader reader);
}
