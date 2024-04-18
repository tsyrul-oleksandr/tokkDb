namespace TokkDb.Data.Documents.Values;

public class ArrayValue : BaseValue {

  public override ValueType Type => ValueType.Array;
  protected override int GetValueBytesCount() {
    throw new NotImplementedException();
  }
}
