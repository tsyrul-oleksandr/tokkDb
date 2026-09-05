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
  public static ushort GetBytesLength(RecordHeader header, ObjectDocument document) {
    var buffer = new BufferSlice(new byte[TokkDb.Configuration.TokkConstants.DefaultPageSize]);
    var writer = new BufferWriter(buffer);
    Write(header, document, writer);
    return (ushort)writer.Position;
  }

  public static void ToBuffer(RecordHeader header, ObjectDocument document, BufferSlice buffer) {
    Write(header, document, new BufferWriter(buffer));
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
