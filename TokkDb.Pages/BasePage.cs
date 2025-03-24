using TokkDb.Buffer;

namespace TokkDb.Pages;

public abstract class BasePage {
  protected const ushort StartHeaderBufferPosition = 0;
  protected const ushort StartContentBufferPosition = 32;
  public abstract PageType Type { get; set; }
  public uint Index { get; set; }
  public PageBuffer Buffer { get; set; }

  public virtual void Load() {
    LoadHeader();
  }

  public virtual void Save() {
    SaveHeader();
  }

  protected virtual int SaveHeader() {
    int position = StartHeaderBufferPosition;
    Buffer.WriteUInt(Index, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteByte((byte)Type, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  protected virtual int LoadHeader() {
    var position = StartHeaderBufferPosition;
    Index = Buffer.Index;
    position += PageBuffer.IndexBufferPosition + TypesConstants.UIntByteSize;
    Type = (PageType)Buffer.ReadByte(position);
    position += TypesConstants.ByteByteSize;
    return position;
  }
}
