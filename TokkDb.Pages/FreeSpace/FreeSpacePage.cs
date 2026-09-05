using TokkDb.Buffer;

namespace TokkDb.Pages;

//A page of the free-space structure of one collection. Entries are packed rather than
//slotted: they are fixed width and never move, so a slot directory would only cost bytes.
public class FreeSpacePage : BasePage {
  public override PageType Type { get; set; } = PageType.FreeSpace;

  public uint NextPageIndex { get; set; }
  public List<FreeSpaceEntry> Entries { get; set; } = [];

  public ushort Capacity =>
    (ushort)((PageSize - StartContentBufferPosition - ControlAreaByteSize) / FreeSpaceEntry.ByteSize);

  public bool IsFull => Entries.Count >= Capacity;

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    EntriesCount = Buffer.ReadUShort(position, out var readBytes);
    position += readBytes;
    NextPageIndex = Buffer.ReadUInt(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUShort((ushort)Entries.Count, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(NextPageIndex, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  protected ushort EntriesCount { get; set; }

  protected override void LoadContent() {
    var position = (int)StartContentBufferPosition;
    Entries = new List<FreeSpaceEntry>(EntriesCount);
    for (var i = 0; i < EntriesCount; i++) {
      var pageIndex = Buffer.ReadUInt(position, out var readBytes);
      position += readBytes;
      var reclaimableBytes = Buffer.ReadUShort(position, out readBytes);
      position += readBytes;
      var state = (BlockState)Buffer.ReadByte(position, out readBytes);
      position += readBytes;
      Entries.Add(new FreeSpaceEntry(pageIndex, reclaimableBytes, state));
    }
  }

  protected override void SaveContent() {
    var position = (int)StartContentBufferPosition;
    foreach (var entry in Entries) {
      Buffer.WriteUInt(entry.PageIndex, position, out var writeBytes);
      position += writeBytes;
      Buffer.WriteUShort(entry.ReclaimableBytes, position, out writeBytes);
      position += writeBytes;
      Buffer.WriteByte((byte)entry.State, position, out writeBytes);
      position += writeBytes;
    }
  }
}
