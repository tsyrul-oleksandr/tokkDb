namespace TokkDb.Values;

public class NullValue : IValue {
  public ValueTypeEnum Type => ValueTypeEnum.Null;
}
