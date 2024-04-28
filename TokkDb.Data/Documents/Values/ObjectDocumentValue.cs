using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public class ObjectDocumentValue : ObjectValue<IDocumentValue>, IDocumentValue {

  public virtual void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Values.Count);
    foreach (var value in Values) {
      writer.WriteString(value.Key);
      writer.Write(value.Value);
    }
  }

  public virtual void ReadValue(TokkDocumentValueReader reader) {
    Values = new Dictionary<string, IDocumentValue>();
    var count = reader.ReadInt();
    if (count == 0) {
      return;
    }
    for (var i = 0; i < count; i++) {
      var key = reader.ReadString();
      var value = reader.Read();
      Values.Add(key, value);
    }
  }
}
