namespace TokkDb.Buffer;

public class PageBuffer : BufferSlice {
  public const ushort IndexBufferPosition = 0;
  
  public uint Index => ReadUInt(IndexBufferPosition, out _);

  public PageBuffer(byte[] bytes) : base(bytes) { }
}
