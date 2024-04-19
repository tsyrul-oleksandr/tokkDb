namespace TokkDb.Data.Documents.Values;

public class ArrayValue : BaseValue {
  public override ValueType Type => ValueType.Array;
  public BaseValue[] Values { get; set; }

  public ArrayValue() { }
  public ArrayValue(BaseValue[] values) {
    Values = values;
  }
  
  public override void WriteValue(TokkValueWriter writer) {
    throw new NotImplementedException();
  }

  public override void ReadValue(TokkValueReader reader) {
    throw new NotImplementedException();
  }

}
