namespace TokkDb.Data.Documents.Values;

public class IntValue : BaseValue {
  public override ValueType Type => ValueType.Int;
  public int Value { get; set; }

  public IntValue() { }
  public IntValue(int value) {
    Value = value;
  }

  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Value);
  }

  public override void ReadValue(TokkValueReader reader) {
    Value = reader.ReadInt();
  }
}
