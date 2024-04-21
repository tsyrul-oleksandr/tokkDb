using TokkDb.Core.Buffer;
using TokkDb.Core.Reader;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class TokkValueReader : TokkBinaryReader {
  public TokkValueReader(BufferSlice buffer) : base(buffer) { }

  public IDocumentValue Read() {
    var type = (DocumentValueType)ReadByte();
    var value = CreateValueType(type);
    value.ReadValue(this);
    return value;
  }

  protected virtual IDocumentValue CreateValueType(DocumentValueType type) {
    return type switch {
      DocumentValueType.Null => new NullValue(),
      DocumentValueType.Int => new IntValue(),
      DocumentValueType.String => new StringValue(),
      DocumentValueType.Object => new ObjectValue(),
      DocumentValueType.Array => new ArrayValue(),
      _ => throw new NotImplementedException()
    };
  }
}
