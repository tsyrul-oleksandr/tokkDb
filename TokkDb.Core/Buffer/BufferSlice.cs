using System.Text;

namespace TokkDb.Core.Buffer;

public class BufferSlice {
  protected IBufferAddress Address { get; }
  protected Memory<byte> Memory { get; set; }

  public BufferSlice(byte[] bytes) {
    Memory = bytes.AsMemory();
  }
  public BufferSlice(Memory<byte> memory, IBufferAddress address) {
    Address = address;
    Memory = memory.Slice(address.Position, address.Length);
  }

  public BufferSlice Slice(IBufferAddress address) {
    return new BufferSlice(Memory, address);
  }
  
  public virtual byte ReadByte(int index) {
    return Memory.Span[index];
  }
  
  public virtual int ReadInt(int index, out int readBytes) {
    var value = BitConverter.ToInt32(Memory.Span[index..(index + TypesConstants.IntByteSize)]);
    readBytes = TypesConstants.IntByteSize;
    return value;
  }
  
  public virtual uint ReadUInt(int index, out int readBytes) {
    var value = BitConverter.ToUInt32(Memory.Span[index..(index + TypesConstants.UIntByteSize)]);
    readBytes = TypesConstants.UIntByteSize;
    return value;
  }
  
  public virtual byte[] ReadBytes(int count, int index, out int readBytes) {
    var bytes = new byte[count];
    for (var i = 0; i < count; i++) {
      bytes[i] = ReadByte(index + i);
    }
    readBytes = TypesConstants.ByteByteSize * count;
    return bytes;
  }
  
  public virtual string ReadString(int index, out int readBytes) {
    var length = ReadInt(index, out var lenBytes);
    var bytes = ReadBytes(length, index + lenBytes, out var contentBytes);
    readBytes = lenBytes + contentBytes;
    return Encoding.UTF8.GetString(bytes);
  }
  
  public virtual void WriteByte(byte value, int index) {
    Memory.Span[index] = value;
  }
  
  public virtual void WriteInt(int value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteUInt(uint value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteBytes(byte[] values, int index, out int writeBytes) {
    for (var i = 0; i < values.Length; i++) {
      var value = values[i];
      WriteByte(value, index + i);
    }
    writeBytes = TypesConstants.ByteByteSize * values.Length;
  }
  
  public virtual void WriteString(string value, int index, out int writeBytes) {
    var bytes = Encoding.UTF8.GetBytes(value);
    WriteInt(bytes.Length, index, out var lenBytes);
    WriteBytes(bytes, index + lenBytes, out var contentBytes);
    writeBytes = lenBytes + contentBytes;
  }
  
  public virtual byte[] ToArray() {
    return Memory.ToArray();
  }
}
