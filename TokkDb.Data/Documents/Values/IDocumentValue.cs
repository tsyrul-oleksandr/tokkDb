using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values {

  public interface IDocumentValue : IValue {
    void WriteValue(TokkValueWriter writer);
    void ReadValue(TokkDocumentValueReader reader);
  }

}
