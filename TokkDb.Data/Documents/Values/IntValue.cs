using TokkDb.Data.Documents.Buffer;

namespace TokkDb.Data.Documents.Values;

public class IntValue : BaseDocumentValue {
  public override DocumentValueType Type => DocumentValueType.Int;
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
