namespace TokkDb.Data.Documents.Values;

public class IntValue : BaseValue {
  public int Value { get; set; }
  public override ValueType Type => ValueType.Int;

  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteInt(Value);
  }

  public override void ReadValue(TokkValueReader reader) {
    Value = reader.ReadInt();
  }
}
