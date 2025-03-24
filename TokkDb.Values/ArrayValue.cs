namespace TokkDb.Values;

public class ArrayValue<TItem> : IValue where TItem : IValue {
  public ValueTypeEnum Type => ValueTypeEnum.Array;
  public TItem[] Values { get; set; }

  public ArrayValue() : this([]) { }
  public ArrayValue(TItem[] values) {
    Values = values;
  }

}
