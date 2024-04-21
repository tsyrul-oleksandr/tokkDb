using System.Text;
using TokkDb.Core.Buffer;

namespace TokkDb.Core.Writer;

public class TokkBinaryWriter {
  public BufferSlice Buffer { get; }
  public int Position { get; set; }

  public TokkBinaryWriter(BufferSlice buffer) {
    Buffer = buffer;
  }
  
  public void WriteByte(byte value) {
    Buffer.WriteByte(value, Position);
    MovePosition(1);
  }
  
  public void WriteInt(int value) {
    Buffer.WriteInt(value, Position, out var writeBytes);
    MovePosition(writeBytes);
  }
  
  public void WriteBytes(byte[] values) {
    Buffer.WriteBytes(values, Position, out var writeBytes);
    MovePosition(writeBytes);
  }
  
  public void WriteString(string value) {
    Buffer.WriteString(value, Position, out var writeBytes);
    MovePosition(writeBytes);
  }
  
  protected virtual void MovePosition(int count) {
    Position += count;
  }
}
