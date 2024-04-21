using TokkDb.Core.Buffer;
using TokkDb.Core.Pages.Headers;

namespace TokkDb.Core.Pages;

public class PageBuffer : BufferSlice {
  public uint Index { 
    get => ReadUInt(PageHeader.IndexField.Index, out _);
    init => WriteUInt(value, PageHeader.IndexField.Index, out _);
  }
  public PageBuffer(byte[] bytes) : base(bytes) { }
  public PageBuffer(Memory<byte> memory, IBufferAddress address) : base(memory, address) { }
}
