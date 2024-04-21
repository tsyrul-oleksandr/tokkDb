using TokkDb.Data.Documents.Buffer;

namespace TokkDb.Data.Documents.Values {

  public interface IDocumentValue {
    DocumentValueType Type { get; }
    void WriteValue(TokkValueWriter writer);
    void ReadValue(TokkValueReader reader);
  }

}
