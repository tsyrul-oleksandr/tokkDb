using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class ArrayDocumentValue : IDocumentValue {
  
  public ValueTypeEnum Type => ValueTypeEnum.Array;
  public IDocumentValue[] Values { get; set; }

  public ArrayDocumentValue() : this([]) { }
  public ArrayDocumentValue(IDocumentValue[] values) {
    Values = values;
  }
  
  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteInt(Values.Length);
    foreach (var value in Values) {
      writer.Write(value);
    }
  }

  public virtual void ReadValue(BufferReader reader) {
    var count = reader.ReadInt();
    Values = new IDocumentValue[count];
    for (var i = 0; i < count; i++) {
      Values[i] = reader.Read();
    }
  }

}
