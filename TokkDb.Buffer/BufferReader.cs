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
  
  public ushort ReadUShort() {
    var value = Buffer.ReadUShort(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }
  
  public uint ReadUInt() {
    var value = Buffer.ReadUInt(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }
  
  public long ReadLong() {
    var value = Buffer.ReadLong(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }

  public decimal ReadDecimal() {
    var value = Buffer.ReadDecimal(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }

  public DateTime ReadDateTime() {
    var value = Buffer.ReadDateTime(Position, out var readBytes);
    MovePosition(readBytes);
    return value;
  }

  public Guid ReadGuid() {
    var value = Buffer.ReadGuid(Position, out var readBytes);
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
