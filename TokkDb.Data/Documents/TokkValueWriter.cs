using TokkDb.Core.Buffer;
using TokkDb.Core.Writer;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class TokkValueWriter : TokkBinaryWriter {
  protected const byte TypeBytesCount = 1;
  
  public TokkValueWriter(BufferSlice buffer) : base(buffer) { }
  
  public void Write(IDocumentValue value) {
    WriteByte((byte)value.Type);
    value.WriteValue(this);
  }
}
