namespace TokkDb.Data.Documents.Values;

public abstract class BaseValue : IDocumentItem {
  public abstract ValueType Type { get; }
  public abstract void WriteValue(TokkValueWriter writer);
  public abstract void ReadValue(TokkValueReader reader);
}
