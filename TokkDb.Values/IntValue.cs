namespace TokkDb.Values;

public class IntValue : IValue {
  public ValueTypeEnum Type => ValueTypeEnum.Int;
  public int Value { get; set; }

  public IntValue() { }
  public IntValue(int value) {
    Value = value;
  }
}
