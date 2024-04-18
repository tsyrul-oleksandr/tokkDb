using TokkDb.Core.Buffer;
using TokkDb.Core.Pages;
using TokkDb.Data.Documents;

namespace TokkDb.Data.Pages;

public class DataPage : BasePage<DataPageHeader> {
  public DataPage(TokkBuffer buffer) : base(buffer) { }

  public void Add(TokkDocument document) {
    
  }
}
