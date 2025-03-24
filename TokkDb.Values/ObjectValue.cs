namespace TokkDb.Values;

public class ObjectValue<TItem> : IValue where TItem : IValue {
  public ValueTypeEnum Type => ValueTypeEnum.Object;
  public Dictionary<string, TItem> Values { get; set; }

  public ObjectValue() : this(new Dictionary<string, TItem>()) { }
  public ObjectValue(Dictionary<string, TItem> values) {
    Values = values;
  }
  
  public TItem this[string key] => Values[key];
}
