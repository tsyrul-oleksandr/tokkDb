namespace TokkDb.Data.Documents.Values;

public class ArrayValue : BaseDocumentValue {
  public override DocumentValueType Type => DocumentValueType.Array;
  public IDocumentValue[] Values { get; set; }

  public ArrayValue() { }
  public ArrayValue(IDocumentValue[] values) {
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
    Values = new IDocumentValue[count];
    for (var i = 0; i < count; i++) {
      Values[i] = reader.Read();
    }
  }

}
