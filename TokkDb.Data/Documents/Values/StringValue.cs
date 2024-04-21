namespace TokkDb.Data.Documents.Values;

public class StringValue : BaseDocumentValue {
  public override DocumentValueType Type => DocumentValueType.String;
  public string Value { get; set; }

  public StringValue() { }
  public StringValue(string value) {
    Value = value;
  }
  
  public override void WriteValue(TokkValueWriter writer) {
    writer.WriteString(Value);
  }

  public override void ReadValue(TokkValueReader reader) {
    Value = reader.ReadString();
  }
}
