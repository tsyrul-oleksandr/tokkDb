namespace TokkDb.Data.Documents.Values;

public class StringValue : BaseValue {

  public override ValueType Type => ValueType.String;

  protected override int GetValueBytesCount() {
    return 0;
  }
}
