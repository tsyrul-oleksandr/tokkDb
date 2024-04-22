using TokkDb.Core.Buffer;
using TokkDb.Core.Writer;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents.Buffer;

public class TokkValueWriter : TokkBinaryWriter {
  
  public TokkValueWriter(BufferSlice buffer) : base(buffer) { }
  
  public void Write(IDocumentValue value) {
    WriteByte((byte)value.Type);
    value.WriteValue(this);
  }
}
