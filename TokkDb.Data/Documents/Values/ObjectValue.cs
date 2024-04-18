namespace TokkDb.Data.Documents.Values;

public class ObjectValue : BaseValue {
  public Dictionary<string, BaseValue> Values { get; set; } = new();
  public override ValueType Type => ValueType.Object;

  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Values.Count);
    if (Values is null) {
      return;
    }
    foreach (var value in Values) {
      writer.WriteString(value.Key);
      writer.Write(value.Value);
    }
  }

  public override void ReadValue(TokkValueReader reader) {
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
