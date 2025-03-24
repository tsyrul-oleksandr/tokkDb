using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class ObjectDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Object;
  public Dictionary<string, IDocumentValue> Values { get; set; }

  public ObjectDocumentValue() : this(new Dictionary<string, IDocumentValue>()) { }
  public ObjectDocumentValue(Dictionary<string, IDocumentValue> values) {
    Values = values;
  }
  
  public IDocumentValue this[string key] => Values[key];
  
  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteInt(Values.Count);
    foreach (var value in Values) {
      writer.WriteString(value.Key);
      writer.Write(value.Value);
    }
  }
  public virtual void ReadValue(BufferReader reader) {
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
