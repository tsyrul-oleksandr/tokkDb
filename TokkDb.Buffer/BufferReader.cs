namespace TokkDb.Buffer;

public class BufferReader {
  private BufferSlice Buffer { get; }
  private int Position { get; set; }
  
  public BufferReader(BufferSlice buffer, int position = default) {
    Buffer = buffer;
    Position = position;
  }
  
  public byte ReadByte() {
    var value = Buffer.ReadByte(Position);
    MovePosition(1);
    return value;
  }
  
  public int ReadInt() {
    var value = Buffer.ReadInt(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }
  
  public byte[] ReadBytes(int count) {
    var value = Buffer.ReadBytes(count, Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }
  
  public string ReadString() {
    var value = Buffer.ReadString(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }

  protected virtual void MovePosition(int count) {
    Position += count;
  }
}
