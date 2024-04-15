namespace TokkDb.Core.Buffer;

public class TokkBuffer {
  public byte[] Bytes { get; }

  public TokkBuffer(byte[] bytes) {
    Bytes = bytes;
  }

  public uint ReadUInt(int index) {
    return Bytes[index] | (uint)Bytes[index + 1] << 8 | (uint)Bytes[index + 2] << 16 | (uint)Bytes[index + 3] << 24;
  }
  
  public byte ReadByte(int index) {
    return Bytes[index];
  }

  public void Write(uint value, int index) {
    Bytes[index] = (byte)value;
    Bytes[index + 1] = (byte)(value >> 8);
    Bytes[index + 2] = (byte)(value >> 16);
    Bytes[index + 3] = (byte)(value >> 24);
  }
}
