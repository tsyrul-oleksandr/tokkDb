using TokkDb.Data.Documents.Buffer;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public class ArrayDocumentValue : ArrayValue<IDocumentValue>, IDocumentValue {
  
  public virtual void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Values.Length);
    foreach (var value in Values) {
      writer.Write(value);
    }
  }

  public virtual void ReadValue(TokkDocumentValueReader reader) {
    var count = reader.ReadInt();
    Values = new IDocumentValue[count];
    for (var i = 0; i < count; i++) {
      Values[i] = reader.Read();
    }
  }

}
