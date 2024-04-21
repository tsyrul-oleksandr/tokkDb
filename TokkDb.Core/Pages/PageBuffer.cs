using TokkDb.Core.Buffer;

namespace TokkDb.Core.Pages;

public class PageBuffer : BufferSlice {
  public uint Index {
    get => ReadUInt(BasePage.HeaderStartPosition, out _);
    init => WriteUInt(value, BasePage.HeaderStartPosition, out _);
  }
  public PageBuffer(byte[] bytes) : base(bytes) { }
  public PageBuffer(Memory<byte> memory, IBufferAddress address) : base(memory, address) { }
}
