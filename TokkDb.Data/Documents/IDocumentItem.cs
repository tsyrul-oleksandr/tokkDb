using TokkDb.Data.Documents.Values;
using ValueType = TokkDb.Data.Documents.Values.ValueType;

namespace TokkDb.Data.Documents;

public interface IDocumentItem {
  ValueType Type { get; }
  void WriteValue(TokkValueWriter writer);
  void ReadValue(TokkValueReader reader);
}
