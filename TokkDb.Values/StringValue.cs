namespace TokkDb.Values;

public class StringValue : IValue {
  public ValueTypeEnum Type => ValueTypeEnum.String;
  public string Value { get; set; }

  public StringValue() : this(string.Empty) { }
  public StringValue(string value) {
    Value = value;
  }
}
