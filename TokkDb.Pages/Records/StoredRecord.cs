using TokkDb.Buffer;
using TokkDb.Documents;
using TokkDb.Documents.Values;

namespace TokkDb.Pages;

//A record as a page holds it: the VR-11 header followed by the document body.
//
//The body carries the document's value only. Its identifier is the header's RecordId, so
//the Ulid is stored once rather than twice — which is why the header costs about 24 bytes
//per record over what was stored before it, not the full 41 it occupies.
public record StoredRecord(RecordHeader Header, ObjectDocument Document);

public static class StoredRecordUtilities {
  //Counted rather than written, and not capped at a page: a record may need an overflow
  //chain (ST-5).
  public static int GetBytesLength(RecordHeader header, ObjectDocument document) {
    var writer = new BufferWriter(new CountingBufferSlice());
    Write(header, document, writer);
    return writer.Position;
  }

  //The whole record image. Splitting it across an overflow chain is the storage layer's
  //business, so it gets the bytes rather than a buffer to write into.
  public static byte[] ToBytes(RecordHeader header, ObjectDocument document) {
    var bytes = new byte[GetBytesLength(header, document)];
    Write(header, document, new BufferWriter(new BufferSlice(bytes)));
    return bytes;
  }

  public static void ToBuffer(RecordHeader header, ObjectDocument document, BufferSlice buffer) {
    Write(header, document, new BufferWriter(buffer));
  }

  //The header alone, for callers that only need to know whose image this is and whether it
  //is still live. It costs no document parsing.
  public static RecordHeader ReadHeader(BufferSlice buffer) {
    return RecordHeader.Read(new BufferReader(buffer));
  }

  //Rewrites the header of an image that is already on a page. Only the header changes: the
  //document body of a stored record is never mutated in place (VR-12).
  public static RecordHeader ReadHeaderFrom(byte[] recordBytes) {
    return RecordHeader.Read(new BufferReader(new BufferSlice(recordBytes)));
  }

  public static void WriteHeader(RecordHeader header, BufferSlice buffer) {
    header.Write(new BufferWriter(buffer));
  }

  public static StoredRecord FromBuffer(BufferSlice buffer) {
    var reader = new BufferReader(buffer);
    var header = RecordHeader.Read(reader);
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(header.RecordId));
    document.SetValue(reader.Read());
    return new StoredRecord(header, document);
  }

  private static void Write(RecordHeader header, ObjectDocument document, BufferWriter writer) {
    header.Write(writer);
    writer.Write(document.Value);
  }
}
