using System.Text;
using TokkDb.Core.Buffer;

namespace TokkDb.Core.Reader;

public class TokkBinaryReader {
  public TokkBuffer Buffer { get; }
  public int Position { get; set; }

  public TokkBinaryReader(TokkBuffer buffer) {
    Buffer = buffer;
  }
  
  public byte ReadByte() {
    var value = Buffer.ReadByte(Position);
    MovePosition(1);
    return value;
  }
  
  public int ReadInt() {
    var value = (Buffer.ReadByte(Position) 
      | Buffer.ReadByte(Position + 1)) 
      | Buffer.ReadByte(Position + 2) 
      | Buffer.ReadByte(Position + 3);
    MovePosition(4);
    return value;
  }
  
  public byte[] ReadBytes(int count) {
    var bytes = new byte[count];
    for (var i = 0; i < count; i++) {
      bytes[0] = Buffer.ReadByte(Position + i);
    }
    MovePosition(count);
    return bytes;
  }
  
  public string ReadString() {
    var length = ReadInt();
    var bytes = ReadBytes(length);
    MovePosition(bytes.Length);
    return Encoding.UTF8.GetString(bytes);
  }

  protected virtual void MovePosition(int count) {
    Position += count;
  }
}
