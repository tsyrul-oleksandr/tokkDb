using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents;

public interface IDocumentValue : IValue {
  void WriteValue(BufferWriter writer);
  void ReadValue(BufferReader reader);
}
