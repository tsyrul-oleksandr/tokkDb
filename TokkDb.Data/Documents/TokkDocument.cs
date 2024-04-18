using TokkDb.Core.Address;
using TokkDb.Core.Buffer;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class TokkDocument {
  public BufferAddress Address { get; set; }
  public DocumentIdentifierType Type { get; set; }
  
  public int GetBytesCount() {
    return 0;
  }
  
  protected virtual int GetIdentifierBytesCount() {
    return ;
  }
}
