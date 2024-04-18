using TokkDb.Core.Buffer;
using TokkDb.Core.Writer;

namespace TokkDb.Data.Documents.Values;

public class TokkValueWriter : TokkBinaryWriter {
  protected const byte TypeBytesCount = 1;
  
  public TokkValueWriter(TokkBuffer buffer) : base(buffer) { }
  
  public void Write(BaseValue value) {
    WriteByte((byte)value.Type);
    value.WriteValue(this);
  }
}
