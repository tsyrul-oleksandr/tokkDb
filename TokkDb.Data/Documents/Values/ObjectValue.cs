namespace TokkDb.Data.Documents.Values;

public class ObjectValue : BaseDocumentValue {
  public override DocumentValueType Type => DocumentValueType.Object;
  public Dictionary<string, IDocumentValue> Values { get; set; }

  public ObjectValue() { }
  public ObjectValue(Dictionary<string, IDocumentValue> values) {
    Values = values;
  }
  
  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Values.Count);
    foreach (var value in Values) {
      writer.WriteString(value.Key);
      writer.Write(value.Value);
    }
  }

  public override void ReadValue(TokkValueReader reader) {
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
