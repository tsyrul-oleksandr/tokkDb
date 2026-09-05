using TokkDb.Buffer;

namespace TokkDb.Pages;

//The checksum a page carries in its control area.
public static class PageChecksum {
  public static uint Compute(BufferSlice buffer, int length) {
    return Compute(buffer.AsReadOnlySpan(0, length));
  }

  public static uint Compute(ReadOnlySpan<byte> bytes) {
    return Crc32.Compute(bytes);
  }
}
