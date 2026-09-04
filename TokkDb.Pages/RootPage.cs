using System.Text;
using TokkDb.Buffer;

namespace TokkDb.Pages;

//The header of an existing file, read before its page size is known.
public record RootPagePrefix(string MagicNumber, ushort FormatVersion, ushort PageSize);

//Page 0. The database describes itself here: this is the one layout that cannot change
//without a magic number change, because every other page is found through it.
//It carries no items, so its header is free to run past StartContentBufferPosition.
public class RootPage : BasePage {
  public const string ExpectedMagicNumber = "TOKKDB01";
  public const ushort CurrentFormatVersion = 2;
  public const byte MagicNumberByteSize = 8;

  //Everything needed to identify the file and size its pages lives inside this prefix,
  //which is why the prefix has to be smaller than the smallest legal page.
  public const int PrefixByteSize = 32;

  private const int MagicNumberPosition = BaseHeaderByteSize;
  private const int FormatVersionPosition = MagicNumberPosition + MagicNumberByteSize;
  private const int PageSizePosition = FormatVersionPosition + TypesConstants.UShortByteSize;

  public override PageType Type { get; set; } = PageType.Root;
  public string MagicNumber { get; set; } = ExpectedMagicNumber;
  public ushort FormatVersion { get; set; } = CurrentFormatVersion;
  public uint CollectionsFirstPageId { get; set; }
  public uint CollectionsPrimaryIndexRoot { get; set; }
  public DateTime CreatedAt { get; set; }
  public uint LastAllocatedPageId { get; set; }

  //Reads and checks the identifying bytes of a file. Nothing else in the file is touched
  //until both the magic number and the format version are the ones this build writes.
  public static RootPagePrefix ReadPrefix(BufferSlice prefix) {
    var magicNumberBytes = prefix.ReadBytes(MagicNumberByteSize, MagicNumberPosition, out _);
    var magicNumber = Encoding.ASCII.GetString(magicNumberBytes);
    if (magicNumber != ExpectedMagicNumber) {
      throw UnsupportedFormatVersionException.ForMagicNumber(magicNumberBytes, ExpectedMagicNumber,
        CurrentFormatVersion);
    }
    var formatVersion = prefix.ReadUShort(FormatVersionPosition, out _);
    if (formatVersion != CurrentFormatVersion) {
      throw UnsupportedFormatVersionException.ForFormatVersion(magicNumber, formatVersion, CurrentFormatVersion);
    }
    var pageSize = prefix.ReadUShort(PageSizePosition, out _);
    return new RootPagePrefix(magicNumber, formatVersion, pageSize);
  }

  //The magic number occupies a fixed width whatever the string it is built from.
  private byte[] GetMagicNumberBytes() {
    var bytes = new byte[MagicNumberByteSize];
    Encoding.ASCII.GetBytes(MagicNumber, 0, Math.Min(MagicNumber.Length, MagicNumberByteSize), bytes, 0);
    return bytes;
  }

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    MagicNumber = Encoding.ASCII.GetString(Buffer.ReadBytes(MagicNumberByteSize, position, out var readBytes));
    position += readBytes;
    FormatVersion = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    PageSize = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    CollectionsFirstPageId = Buffer.ReadUInt(position, out readBytes);
    position += readBytes;
    CollectionsPrimaryIndexRoot = Buffer.ReadUInt(position, out readBytes);
    position += readBytes;
    CreatedAt = Buffer.ReadDateTime(position, out readBytes);
    position += readBytes;
    LastAllocatedPageId = Buffer.ReadUInt(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteBytes(GetMagicNumberBytes(), position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(FormatVersion, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(PageSize, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(CollectionsFirstPageId, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(CollectionsPrimaryIndexRoot, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteDateTime(CreatedAt, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUInt(LastAllocatedPageId, position, out writeBytes);
    position += writeBytes;
    return position;
  }
}
