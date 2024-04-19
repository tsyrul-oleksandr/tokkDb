namespace TokkDb.Data.Documents.Values;

public class ArrayValue : BaseValue {
  public override ValueType Type => ValueType.Array;
  public BaseValue[] Values { get; set; }

  public ArrayValue() { }
  public ArrayValue(BaseValue[] values) {
    Values = values;
  }
  
  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Values.Length);
    foreach (var value in Values) {
      writer.Write(value);
    }
  }

  public override void ReadValue(TokkValueReader reader) {
    var count = reader.ReadInt();
    Values = new BaseValue[count];
    for (var i = 0; i < count; i++) {
      Values[i] = reader.Read();
    }
  }

}
