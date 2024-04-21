namespace TokkDb.Data.Documents.Values;

public class NullValue : BaseDocumentValue {
  public override DocumentValueType Type => DocumentValueType.Null;
  
  public override void WriteValue(TokkValueWriter writer) {
    
  }

  public override void ReadValue(TokkValueReader reader) {
    
  }
}
