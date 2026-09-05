using TokkDb.Buffer;

namespace TokkDb.Pages.Indexes;

//An interior node. It holds separator keys and child pointers and no entries at all: a key
//found here is a signpost, and the record it came from is still down in a leaf. That is why
//an interior node fans out so widely, and why the tree stays shallow.
public class IndexInteriorPage : BaseIndexPage {
  public override PageType Type { get; set; } = PageType.IndexInterior;

  //The child holding everything below the first separator. Keeping it out of the list is
  //what makes "n separators, n + 1 children" a fact of the layout rather than a convention
  //the reader has to remember.
  public uint FirstChildPageIndex { get; set; }

  public List<IndexSeparator> Entries { get; set; } = [];

  public int ChildCount => Entries.Count + 1;

  public override int ContentByteSize {
    get {
      var total = 0;
      foreach (var entry in Entries) {
        total += EntryByteSize(entry.Key, TypesConstants.UIntByteSize);
      }
      return total;
    }
  }

  public static int SizeOf(IndexSeparator separator) {
    return EntryByteSize(separator.Key, TypesConstants.UIntByteSize);
  }

  //The child at a position, counting FirstChildPageIndex as position zero.
  public uint ChildAt(int position) {
    return position == 0 ? FirstChildPageIndex : Entries[position - 1].ChildPageIndex;
  }

  public void SetChildAt(int position, uint pageIndex) {
    if (position == 0) {
      FirstChildPageIndex = pageIndex;
    } else {
      Entries[position - 1] = Entries[position - 1] with { ChildPageIndex = pageIndex };
    }
  }

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    EntriesCount = Buffer.ReadUShort(position, out var readBytes);
    position += readBytes;
    FirstChildPageIndex = Buffer.ReadUInt(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUShort((ushort)Entries.Count, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(FirstChildPageIndex, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  protected override void LoadContent() {
    var position = (int)StartContentBufferPosition;
    Entries = new List<IndexSeparator>(EntriesCount);
    for (var i = 0; i < EntriesCount; i++) {
      var key = ReadKey(ref position);
      Entries.Add(new IndexSeparator(key, ReadPageIndex(ref position)));
    }
  }

  protected override void SaveContent() {
    var position = (int)StartContentBufferPosition;
    foreach (var entry in Entries) {
      WriteKey(entry.Key, ref position);
      WritePageIndex(entry.ChildPageIndex, ref position);
    }
  }
}
