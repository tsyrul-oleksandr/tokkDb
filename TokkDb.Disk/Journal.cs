using TokkDb.Buffer;

namespace TokkDb.Disk;

//The rollback journal that sits beside the database file. It holds what the pages of the
//running transaction looked like *before* it touched them, so that a transaction
//interrupted part way through its writes can be taken back out of the database file.
//
//The commit protocol is: write the before images here and flush them, write the pages to
//the database file and flush that, and only then record the commit. A frame without a
//commit record therefore means the database file may hold a half applied transaction, and
//the before images in this file are what puts it back.
public class Journal : IDisposable {
  public const string FileExtension = ".wal";
  public const string MagicNumber = "TOKKWAL1";
  public const ushort CurrentFormatVersion = 1;
  public const uint CommitMarker = 0x434F4D4D;

  private const int MagicNumberByteSize = 8;
  private const int HeaderByteSize = MagicNumberByteSize + TypesConstants.UShortByteSize * 2 +
    TypesConstants.LongByteSize + TypesConstants.UIntByteSize * 2 + TypesConstants.UIntByteSize;
  private const int PageImagePrefixByteSize = TypesConstants.UIntByteSize + TypesConstants.ByteByteSize;
  private const int CommitRecordByteSize = TypesConstants.UIntByteSize + TypesConstants.LongByteSize +
    TypesConstants.UIntByteSize;

  private readonly FileStream _stream;
  private bool _disposed;

  public string FilePath { get; }
  public ushort PageSize { get; set; }

  public Journal(string databaseFilePath, ushort pageSize) {
    FilePath = GetJournalPath(databaseFilePath);
    PageSize = pageSize;
    _stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
  }

  public static string GetJournalPath(string databaseFilePath) {
    return databaseFilePath + FileExtension;
  }

  public long Length => _stream.Length;

  //Starts a frame, discarding whatever was there. Anything already in the file belongs to a
  //transaction that finished, so it has nothing left to say.
  public void Begin(ulong transactionId, uint originalPageCount, int pageImageCount) {
    _stream.SetLength(0);
    _stream.Position = 0;
    var header = new BufferSlice(new byte[HeaderByteSize]);
    var position = 0;
    header.WriteBytes(GetMagicNumberBytes(), position, out var writeBytes);
    position += writeBytes;
    header.WriteUShort(CurrentFormatVersion, position, out writeBytes);
    position += writeBytes;
    header.WriteUShort(PageSize, position, out writeBytes);
    position += writeBytes;
    header.WriteLong(unchecked((long)transactionId), position, out writeBytes);
    position += writeBytes;
    header.WriteUInt(originalPageCount, position, out writeBytes);
    position += writeBytes;
    header.WriteUInt((uint)pageImageCount, position, out writeBytes);
    position += writeBytes;
    header.WriteUInt(Crc32.Compute(header.AsReadOnlySpan(0, position)), position, out _);
    Write(header.ToArray());
  }

  //A null image records a page that did not exist before the transaction: undoing it means
  //truncating the file back, not restoring content.
  public void WriteBeforeImage(uint pageIndex, byte[] beforeImage) {
    var record = new BufferSlice(new byte[PageImagePrefixByteSize]);
    record.WriteUInt(pageIndex, 0, out var writeBytes);
    record.WriteByte(beforeImage is null ? (byte)0 : (byte)1, writeBytes, out _);
    var prefix = record.ToArray();
    var checksum = beforeImage is null
      ? Crc32.Compute(prefix)
      : Crc32.Compute([.. prefix, .. beforeImage]);
    Write(prefix);
    if (beforeImage is not null) {
      Write(beforeImage);
    }
    var checksumBytes = new BufferSlice(new byte[TypesConstants.UIntByteSize]);
    checksumBytes.WriteUInt(checksum, 0, out _);
    Write(checksumBytes.ToArray());
  }

  //Forces the before images onto the device. Nothing may touch the database file until this
  //has returned.
  public void Flush() {
    _stream.Flush(flushToDisk: true);
  }

  //The last step of the commit protocol, after the database file itself is durable.
  public void MarkCommitted(ulong transactionId) {
    _stream.Position = _stream.Length;
    var record = new BufferSlice(new byte[CommitRecordByteSize]);
    var position = 0;
    record.WriteUInt(CommitMarker, position, out var writeBytes);
    position += writeBytes;
    record.WriteLong(unchecked((long)transactionId), position, out writeBytes);
    position += writeBytes;
    record.WriteUInt(Crc32.Compute(record.AsReadOnlySpan(0, position)), position, out _);
    Write(record.ToArray());
    Flush();
  }

  //Reads the frame back. This is the parser recovery needs; applying what it finds is the
  //next step's work.
  public JournalFrame Read() {
    if (_stream.Length < HeaderByteSize) {
      return null;
    }
    _stream.Position = 0;
    var header = ReadSlice(HeaderByteSize);
    var position = 0;
    var magicNumber = System.Text.Encoding.ASCII.GetString(
      header.ReadBytes(MagicNumberByteSize, position, out var readBytes));
    position += readBytes;
    if (magicNumber != MagicNumber) {
      throw new JournalCorruptedException(
        $"'{FilePath}' does not begin with the journal magic number '{MagicNumber}'.");
    }
    var formatVersion = header.ReadUShort(position, out readBytes);
    position += readBytes;
    var pageSize = header.ReadUShort(position, out readBytes);
    position += readBytes;
    var transactionId = unchecked((ulong)header.ReadLong(position, out readBytes));
    position += readBytes;
    var originalPageCount = header.ReadUInt(position, out readBytes);
    position += readBytes;
    var pageImageCount = header.ReadUInt(position, out readBytes);
    position += readBytes;
    var storedChecksum = header.ReadUInt(position, out _);
    if (storedChecksum != Crc32.Compute(header.AsReadOnlySpan(0, position))) {
      throw new JournalCorruptedException($"The header of '{FilePath}' does not match its checksum.");
    }

    var pages = new List<JournalPageImage>((int)pageImageCount);
    for (var i = 0u; i < pageImageCount; i++) {
      var image = ReadPageImage(pageSize);
      if (image is null) {
        //The frame stops in the middle of the images, so the transaction never got as far as
        //the database file and there is nothing of it to undo.
        return new JournalFrame(transactionId, formatVersion, pageSize, originalPageCount, pages, false, false);
      }
      pages.Add(image);
    }
    return new JournalFrame(transactionId, formatVersion, pageSize, originalPageCount, pages, ReadCommitted(), true);
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _stream.Dispose();
  }

  private JournalPageImage ReadPageImage(ushort pageSize) {
    if (_stream.Length - _stream.Position < PageImagePrefixByteSize) {
      return null;
    }
    var prefix = ReadSlice(PageImagePrefixByteSize);
    var pageIndex = prefix.ReadUInt(0, out var readBytes);
    var hasBeforeImage = prefix.ReadByte(readBytes) != 0;
    var remaining = (hasBeforeImage ? pageSize : 0) + TypesConstants.UIntByteSize;
    if (_stream.Length - _stream.Position < remaining) {
      return null;
    }
    var beforeImage = hasBeforeImage ? ReadSlice(pageSize).ToArray() : null;
    var storedChecksum = ReadSlice(TypesConstants.UIntByteSize).ReadUInt(0, out _);
    var prefixBytes = prefix.ToArray();
    var checksum = beforeImage is null
      ? Crc32.Compute(prefixBytes)
      : Crc32.Compute([.. prefixBytes, .. beforeImage]);
    if (storedChecksum != checksum) {
      throw new JournalCorruptedException(
        $"The image of page {pageIndex} in '{FilePath}' does not match its checksum.");
    }
    return new JournalPageImage(pageIndex, beforeImage);
  }

  private bool ReadCommitted() {
    if (_stream.Length - _stream.Position < CommitRecordByteSize) {
      return false;
    }
    var record = ReadSlice(CommitRecordByteSize);
    var marker = record.ReadUInt(0, out var readBytes);
    var position = readBytes + TypesConstants.LongByteSize;
    var storedChecksum = record.ReadUInt(position, out _);
    return marker == CommitMarker && storedChecksum == Crc32.Compute(record.AsReadOnlySpan(0, position));
  }

  private BufferSlice ReadSlice(int length) {
    var bytes = new byte[length];
    _stream.ReadExactly(bytes, 0, length);
    return new BufferSlice(bytes);
  }

  private void Write(byte[] bytes) {
    _stream.Write(bytes, 0, bytes.Length);
  }

  private static byte[] GetMagicNumberBytes() {
    return System.Text.Encoding.ASCII.GetBytes(MagicNumber);
  }
}
