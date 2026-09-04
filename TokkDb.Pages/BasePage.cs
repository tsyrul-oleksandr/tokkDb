using TokkDb.Buffer;

namespace TokkDb.Pages;

//Page layout: header | slot array | record area | free space | control area.
//The header and the control area belong to every page; what sits between them is the
//business of the page kind.
public abstract class BasePage {
  protected const ushort StartHeaderBufferPosition = 0;

  //The fields every page shares: its index, its type and the collection that owns it.
  public const ushort BaseHeaderByteSize =
    TypesConstants.UIntByteSize + TypesConstants.ByteByteSize + TypesConstants.UIntByteSize;

  //Where a page kind may start writing. It leaves room for the fields the kinds add to the
  //shared header, the largest of which is the root page's.
  public const ushort StartContentBufferPosition = 64;

  //The control area closes the page and currently holds the checksum alone.
  public const ushort ControlAreaByteSize = TypesConstants.UIntByteSize;

  public abstract PageType Type { get; set; }
  public uint Index { get; set; }

  //Set from the file the page belongs to; the root page reads it back out of its own buffer.
  public ushort PageSize { get; set; }

  //0 for the pages the engine owns itself, the identifier of the collection for data pages.
  //This is what makes "a page belongs to exactly one collection" a recorded fact.
  public uint OwningCollectionId { get; set; }

  public PageBuffer Buffer { get; set; }

  public int ChecksumPosition => PageSize - ControlAreaByteSize;

  //The checksum is verified before a single field is interpreted: a damaged page is
  //reported, never parsed.
  public void Load() {
    VerifyChecksum();
    LoadHeader();
    LoadContent();
  }

  public void Save() {
    SaveHeader();
    SaveContent();
    SaveChecksum();
  }

  protected virtual void LoadContent() { }

  protected virtual void SaveContent() { }

  protected virtual int SaveHeader() {
    int position = StartHeaderBufferPosition;
    Buffer.WriteUInt(Index, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteByte((byte)Type, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(OwningCollectionId, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  protected virtual int LoadHeader() {
    int position = StartHeaderBufferPosition;
    Index = Buffer.Index;
    position += PageBuffer.IndexBufferPosition + TypesConstants.UIntByteSize;
    Type = (PageType)Buffer.ReadByte(position);
    position += TypesConstants.ByteByteSize;
    OwningCollectionId = Buffer.ReadUInt(position, out var readBytes);
    position += readBytes;
    return position;
  }

  //Covers every byte in front of the control area, so no edit to the page can leave the
  //checksum agreeing with the content by accident.
  private void SaveChecksum() {
    var checksum = PageChecksum.Compute(Buffer, ChecksumPosition);
    Buffer.WriteUInt(checksum, ChecksumPosition, out _);
  }

  private void VerifyChecksum() {
    var storedChecksum = Buffer.ReadUInt(ChecksumPosition, out _);
    var computedChecksum = PageChecksum.Compute(Buffer, ChecksumPosition);
    if (storedChecksum != computedChecksum) {
      throw new PageCorruptedException(Index, storedChecksum, computedChecksum);
    }
  }
}
