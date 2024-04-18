namespace TokkDb.Data.Documents.Values;

public class NullValue : BaseValue {
  public override ValueType Type => ValueType.Null;
  public override void WriteValue(TokkValueWriter writer) {
    
  }

  public override void ReadValue(TokkValueReader reader) {
    
  }
}
