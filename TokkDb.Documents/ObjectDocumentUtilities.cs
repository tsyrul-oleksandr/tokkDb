using TokkDb.Buffer;

using TokkDb.Documents.Values;

namespace TokkDb.Documents;

public class ObjectDocumentUtilities {
  public static ObjectDocument FromBuffer(BufferSlice buffer) {
    var reader = new BufferReader(buffer);
    var document = new ObjectDocument();
    document.Read(reader);
    return document;
  }
  
  public static void ToBuffer(ObjectDocument document, BufferSlice buffer) {
    var writer = new BufferWriter(buffer);
    document.Write(writer);
  }
  
  //Counts the bytes the document would take without writing them anywhere, and returns a
  //count that is not capped at a page: a document may be far larger than one (ST-5).
  public static int GetBytesLength(ObjectDocument document) {
    var writer = new BufferWriter(new CountingBufferSlice());
    document.Write(writer);
    return writer.Position;
  }

  public static int GetValueBytesLength(IDocumentValue value) {
    var writer = new BufferWriter(new CountingBufferSlice());
    writer.Write(value);
    return writer.Position;
  }
}
