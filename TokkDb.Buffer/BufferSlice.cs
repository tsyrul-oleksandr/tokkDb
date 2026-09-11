using System.Text;

namespace TokkDb.Buffer;

public class BufferSlice {
  private readonly Memory<byte> _buffer;

  public BufferSlice(Memory<byte> buffer) {
    _buffer = buffer;
  }

  public int Length => _buffer.Length;

  public BufferSlice Slice(int position, int length) {
    return new BufferSlice(_buffer.Slice(position, length));
  }
  
  //A window over the bytes themselves, for readers that work on the whole run at once.
  public ReadOnlySpan<byte> AsReadOnlySpan(int index, int count) {
    return _buffer.Span.Slice(index, count);
  }

  public virtual byte ReadByte(int index) {
    return _buffer.Span[index];
  }
  
  public virtual byte ReadByte(int index, out int readBytes) {
    var value = _buffer.Span[index];
    readBytes = TypesConstants.ByteByteSize;
    return value;
  }
  
  public virtual short ReadShort(int index) {
    return BitConverter.ToInt16(_buffer.Span[index..(index + TypesConstants.ShortByteSize)]);
  }
  
  public virtual short ReadShort(int index, out int readBytes) {
    readBytes = TypesConstants.ShortByteSize;
    return ReadShort(index);
  }
  
  public virtual ushort ReadUShort(int index) {
    return BitConverter.ToUInt16(_buffer.Span[index..(index + TypesConstants.UShortByteSize)]);
  }
  
  public virtual ushort ReadUShort(int index, out int readBytes) {
    readBytes = TypesConstants.UShortByteSize;
    return ReadUShort(index);
  }
  
  public virtual int ReadInt(int index, out int readBytes) {
    var value = BitConverter.ToInt32(_buffer.Span[index..(index + TypesConstants.IntByteSize)]);
    readBytes = TypesConstants.IntByteSize;
    return value;
  }
  
  public virtual uint ReadUInt(int index, out int readBytes) {
    var value = BitConverter.ToUInt32(_buffer.Span[index..(index + TypesConstants.UIntByteSize)]);
    readBytes = TypesConstants.UIntByteSize;
    return value;
  }
  
  public long ReadLong(int index, out int readBytes) {
    var value = BitConverter.ToInt64(_buffer.Span[index..(index + TypesConstants.LongByteSize)]);
    readBytes = TypesConstants.LongByteSize;
    return value;
  }
  
  //Four ints in the order decimal.GetBits gives them: lo, mid, hi, flags. Round-tripping
  //through the bits rather than through text keeps the scale, so 1.50 comes back as 1.50 and
  //not as 1.5 — decimal distinguishes the two and the key encoding of D-3 depends on it.
  //
  //Through ReadBytes and WriteBytes like every other type here, and virtual for the same
  //reason: a slice that only counts what a write would cost has no buffer to index into, and
  //a writer that reached past the virtual pair would fail there rather than measure.
  public virtual decimal ReadDecimal(int index, out int readBytes) {
    var bytes = ReadBytes(TypesConstants.DecimalByteSize, index, out readBytes);
    return new decimal([
      BitConverter.ToInt32(bytes, 0),
      BitConverter.ToInt32(bytes, TypesConstants.IntByteSize),
      BitConverter.ToInt32(bytes, TypesConstants.IntByteSize * 2),
      BitConverter.ToInt32(bytes, TypesConstants.IntByteSize * 3)
    ]);
  }

  public virtual void WriteDecimal(decimal value, int index, out int writeBytes) {
    Span<int> bits = stackalloc int[4];
    decimal.GetBits(value, bits);
    var bytes = new byte[TypesConstants.DecimalByteSize];
    for (var i = 0; i < 4; i++) {
      BitConverter.GetBytes(bits[i]).CopyTo(bytes, i * TypesConstants.IntByteSize);
    }
    WriteBytes(bytes, index, out writeBytes);
  }

  //Big endian, the same layout KeyEncoder writes a Guid key in, so the stored bytes and the
  //key bytes are the same sixteen in the same order.
  public virtual Guid ReadGuid(int index, out int readBytes) {
    return new Guid(ReadBytes(TypesConstants.GuidByteSize, index, out readBytes), bigEndian: true);
  }

  public virtual void WriteGuid(Guid value, int index, out int writeBytes) {
    var bytes = new byte[TypesConstants.GuidByteSize];
    value.TryWriteBytes(bytes, bigEndian: true, out _);
    WriteBytes(bytes, index, out writeBytes);
  }

  public DateTime ReadDateTime(int index, out int readBytes) {
    var ticks = BitConverter.ToInt64(_buffer.Span[index..(index + TypesConstants.LongByteSize)]);
    readBytes = TypesConstants.DateTimeByteSize;
    var value = DateTime.FromBinary(ticks);
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
    _buffer.Span[index] = value;
  }
  
  public virtual void WriteByte(byte value, int index, out int writeBytes) {
    WriteByte(value, index);
    writeBytes = TypesConstants.ByteByteSize;
  }
  
  public virtual void WriteShort(short value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteUShort(ushort value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteInt(int value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteUInt(uint value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteLong(long value, int index, out int writeBytes) {
    var bytes = BitConverter.GetBytes(value);
    WriteBytes(bytes, index, out writeBytes);
  }
  
  public virtual void WriteDateTime(DateTime value, int index, out int writeBytes) {
    var ticks = value.ToBinary();
    WriteLong(ticks, index, out writeBytes);
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
  
  //Moves bytes inside the buffer. The regions may overlap, which is what compaction does
  //when it slides records down over the gaps in front of them.
  public virtual void MoveBytes(int fromIndex, int toIndex, int length) {
    _buffer.Span.Slice(fromIndex, length).CopyTo(_buffer.Span.Slice(toIndex, length));
  }

  public virtual byte[] ToArray() {
    return _buffer.ToArray();
  }
}
