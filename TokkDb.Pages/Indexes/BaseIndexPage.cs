using TokkDb.Buffer;

namespace TokkDb.Pages.Indexes;

//What the two node kinds of a B+Tree share: variable-length keys on the page, and the fill
//rules that decide when the tree has to change shape.
//
//Entries are held as a list and written out whole, the way FreeSpacePage holds its entries,
//rather than through a slot directory. A node is read once per transaction and rewritten
//when it changes, and its entries have to stay in key order — a directory whose slots were
//handed out in insertion order would have to be re-sorted on every write anyway.
public abstract class BaseIndexPage : BasePage {
  //Everything between the header and the control area. Splitting on bytes rather than on a
  //count is what keeps the rule honest for keys that are not all the same length.
  public ushort UsableBytes => (ushort)(PageSize - StartContentBufferPosition - ControlAreaByteSize);

  //Half full is the classic B+Tree floor, and what makes the tree's depth logarithmic
  //rather than merely finite.
  public ushort MinimumBytes => (ushort)(UsableBytes / 2);

  public abstract int ContentByteSize { get; }

  public bool IsOverfull => ContentByteSize > UsableBytes;

  public bool IsUnderfull => ContentByteSize < MinimumBytes;

  //Read out of the header before the content is parsed, so the reader knows how many
  //variable-length entries to expect.
  protected ushort EntriesCount { get; set; }

  protected static int EntryByteSize(byte[] key, int payloadByteSize) {
    return TypesConstants.UShortByteSize + key.Length + payloadByteSize;
  }

  protected byte[] ReadKey(ref int position) {
    var length = Buffer.ReadUShort(position, out var readBytes);
    position += readBytes;
    var key = Buffer.ReadBytes(length, position, out readBytes);
    position += readBytes;
    return key;
  }

  protected void WriteKey(byte[] key, ref int position) {
    Buffer.WriteUShort((ushort)key.Length, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteBytes(key, position, out writeBytes);
    position += writeBytes;
  }

  protected uint ReadPageIndex(ref int position) {
    var value = Buffer.ReadUInt(position, out var readBytes);
    position += readBytes;
    return value;
  }

  protected void WritePageIndex(uint value, ref int position) {
    Buffer.WriteUInt(value, position, out var writeBytes);
    position += writeBytes;
  }
}
