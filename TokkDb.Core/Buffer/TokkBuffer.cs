namespace TokkDb.Core.Buffer;

public class TokkBuffer {
  public byte[] Bytes { get; }

  public TokkBuffer(byte[] bytes) {
    Bytes = bytes;
  }
  
  public byte ReadByte(int index) {
    return Bytes[index];
  }
  
  public void WriteByte(byte value, int index) {
    Bytes[index] = value;
  }
}
