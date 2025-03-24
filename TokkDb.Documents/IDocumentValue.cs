using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents;

public interface IDocumentValue {
  ValueTypeEnum Type { get; }
  void WriteValue(BufferWriter writer);
  void ReadValue(BufferReader reader);
}
