using TokkDb.Core.Buffer;
using TokkDb.Core.Reader;

namespace TokkDb.Data.Documents.Values;

public class TokkValueReader : TokkBinaryReader {
  public TokkValueReader(TokkBuffer buffer) : base(buffer) { }

  public BaseValue Read() {
    var type = (ValueType)ReadByte();
    var value = CreateValueType(type);
    value.ReadValue(this);
    return value;
  }

  protected virtual BaseValue CreateValueType(ValueType type) {
    return type switch {
      ValueType.Null => new NullValue(),
      ValueType.Int => new IntValue(),
      ValueType.String => new StringValue(),
      ValueType.Object => new ObjectValue(),
      _ => throw new NotImplementedException()
    };
  }
}
