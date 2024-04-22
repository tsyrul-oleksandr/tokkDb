using TokkDb.Core.Buffer;

namespace TokkDb.Core.Reader;

public class TokkBinaryReader {
  public BufferSlice Buffer { get; }
  public int Position { get; set; }

  public TokkBinaryReader(BufferSlice buffer) {
    Buffer = buffer;
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
