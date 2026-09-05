using TokkDb.Buffer;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Indexes;

//A leaf of the B+Tree, and the only place an entry ever lives. That is the difference from a
//B-tree, and it is what makes a range scan, an ordered read and a page of results the same
//operation: find the first leaf, then walk right.
public class IndexLeafPage : BaseIndexPage {
  //A page index and a slot index (D-2).
  private const int AddressByteSize = TypesConstants.UIntByteSize + TypesConstants.UShortByteSize;

  public override PageType Type { get; set; } = PageType.IndexLeaf;

  //The right sibling. Zero at the rightmost leaf, which is unambiguous because page 0 is the
  //root page of the file and can never be a leaf.
  public uint NextPageIndex { get; set; }

  public List<IndexEntry> Entries { get; set; } = [];

  public override int ContentByteSize {
    get {
      var total = 0;
      foreach (var entry in Entries) {
        total += EntryByteSize(entry.Key, AddressByteSize);
      }
      return total;
    }
  }

  public static int SizeOf(IndexEntry entry) {
    return EntryByteSize(entry.Key, AddressByteSize);
  }

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

  protected override void LoadContent() {
    var position = (int)StartContentBufferPosition;
    Entries = new List<IndexEntry>(EntriesCount);
    for (var i = 0; i < EntriesCount; i++) {
      var key = ReadKey(ref position);
      var pageIndex = ReadPageIndex(ref position);
      var slotIndex = Buffer.ReadUShort(position, out var readBytes);
      position += readBytes;
      Entries.Add(new IndexEntry(key, new DocumentAddress(pageIndex, slotIndex)));
    }
  }

  protected override void SaveContent() {
    var position = (int)StartContentBufferPosition;
    foreach (var entry in Entries) {
      WriteKey(entry.Key, ref position);
      WritePageIndex(entry.Address.PageIndex, ref position);
      Buffer.WriteUShort(entry.Address.SlotIndex, position, out var writeBytes);
      position += writeBytes;
    }
  }
}
