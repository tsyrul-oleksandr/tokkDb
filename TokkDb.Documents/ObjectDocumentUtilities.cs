using TokkDb.Buffer;
using TokkDb.Configuration;

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
  
  public static ushort GetBytesLength(ObjectDocument document) {
    //todo fix
    var buffer = new BufferSlice(new byte[TokkConstants.PageSize]);
    var writer = new BufferWriter(buffer);
    document.Write(writer);
    return (ushort)writer.Position;
  }
}
