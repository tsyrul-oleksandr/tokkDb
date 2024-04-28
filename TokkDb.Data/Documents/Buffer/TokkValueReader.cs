using TokkDb.Core.Buffer;
using TokkDb.Core.Reader;
using TokkDb.Data.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Buffer;

public class TokkDocumentValueReader : TokkBinaryReader {
  public TokkDocumentValueReader(BufferSlice buffer) : base(buffer) { }

  public IDocumentValue Read() {
    var type = (ValueType)ReadByte();
    var value = CreateValueType(type);
    value.ReadValue(this);
    return value;
  }

  protected virtual IDocumentValue CreateValueType(ValueType type) {
    return type switch {
      ValueTypeEnum.Null => new NullDocumentValue(),
      ValueTypeEnum.Int => new IntDocumentValue(),
      ValueTypeEnum.String => new StringDocumentValue(),
      ValueTypeEnum.Object => new ObjectDocumentValue(),
      ValueTypeEnum.Array => new ArrayDocumentValue(),
      _ => throw new NotImplementedException()
    };
  }
}
