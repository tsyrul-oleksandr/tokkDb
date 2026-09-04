using TokkDb.Buffer;
using TokkDb.Configuration;
using Xunit;

namespace TokkDb.Tests;

public class BufferRoundTripTests {
  private static BufferSlice NewSlice() {
    return new BufferSlice(new byte[TokkConstants.PageSize]);
  }

  [Theory]
  [InlineData((byte)0)]
  [InlineData((byte)1)]
  [InlineData(byte.MaxValue)]
  public void ByteRoundTrips(byte value) {
    var slice = NewSlice();
    slice.WriteByte(value, 10, out var writeBytes);
    var read = slice.ReadByte(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData(short.MinValue)]
  [InlineData((short)-1)]
  [InlineData((short)0)]
  [InlineData(short.MaxValue)]
  public void ShortRoundTrips(short value) {
    var slice = NewSlice();
    slice.WriteShort(value, 10, out var writeBytes);
    var read = slice.ReadShort(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData((ushort)0)]
  [InlineData((ushort)32)]
  [InlineData(ushort.MaxValue)]
  public void UShortRoundTrips(ushort value) {
    var slice = NewSlice();
    slice.WriteUShort(value, 10, out var writeBytes);
    var read = slice.ReadUShort(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData(int.MinValue)]
  [InlineData(-1)]
  [InlineData(0)]
  [InlineData(int.MaxValue)]
  public void IntRoundTrips(int value) {
    var slice = NewSlice();
    slice.WriteInt(value, 10, out var writeBytes);
    var read = slice.ReadInt(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData(0u)]
  [InlineData(1u)]
  [InlineData(uint.MaxValue)]
  public void UIntRoundTrips(uint value) {
    var slice = NewSlice();
    slice.WriteUInt(value, 10, out var writeBytes);
    var read = slice.ReadUInt(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData(long.MinValue)]
  [InlineData(-1L)]
  [InlineData(0L)]
  [InlineData(uint.MaxValue + 1L)]
  [InlineData(long.MaxValue)]
  public void LongRoundTrips(long value) {
    var slice = NewSlice();
    slice.WriteLong(value, 10, out var writeBytes);
    var read = slice.ReadLong(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Fact]
  public void DateTimeRoundTripsPreservingKindAndTicks() {
    var slice = NewSlice();
    var value = new DateTime(2025, 3, 24, 18, 25, 42, DateTimeKind.Utc).AddTicks(1234);
    slice.WriteDateTime(value, 10, out var writeBytes);
    var read = slice.ReadDateTime(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(value.Kind, read.Kind);
    Assert.Equal(writeBytes, readBytes);
  }

  [Fact]
  public void BytesRoundTrip() {
    var slice = NewSlice();
    var value = new byte[] { 0, 1, 2, 250, 255 };
    slice.WriteBytes(value, 10, out var writeBytes);
    var read = slice.ReadBytes(value.Length, 10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Theory]
  [InlineData("")]
  [InlineData("Ivan")]
  [InlineData("Олександр")]
  [InlineData("emoji \U0001F600 and \"quotes\"")]
  public void StringRoundTrips(string value) {
    var slice = NewSlice();
    slice.WriteString(value, 10, out var writeBytes);
    var read = slice.ReadString(10, out var readBytes);
    Assert.Equal(value, read);
    Assert.Equal(writeBytes, readBytes);
  }

  [Fact]
  public void SliceIsAWindowOntoTheSameMemory() {
    var slice = NewSlice();
    var window = slice.Slice(100, 8);
    window.WriteInt(4242, 0, out _);
    Assert.Equal(4242, slice.ReadInt(100, out _));
  }

  [Fact]
  public void ReaderAndWriterRoundTripASequenceAndAgreeOnPosition() {
    var slice = NewSlice();
    var writer = new BufferWriter(slice);
    writer.WriteByte(7);
    writer.WriteInt(-99);
    writer.WriteString("Pavlo");
    writer.WriteBytes([1, 2, 3]);

    var reader = new BufferReader(slice);
    Assert.Equal(7, reader.ReadByte());
    Assert.Equal(-99, reader.ReadInt());
    Assert.Equal("Pavlo", reader.ReadString());
    Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes(3));
    Assert.Equal(1 + 4 + (4 + 5) + 3, writer.Position);
  }
}
