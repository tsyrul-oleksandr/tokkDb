namespace TokkDb.Buffer;

public class BufferWriter {
  public BufferSlice Buffer { get; }
  public int Position { get; set; }

  public BufferWriter(BufferSlice buffer, int position = default) {
    Buffer = buffer;
    Position = position;
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
