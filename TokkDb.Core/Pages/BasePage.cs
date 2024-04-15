using TokkDb.Core.Buffer;
using TokkDb.Core.Pages.Headers;

namespace TokkDb.Core.Pages;

public class BasePage<THeader> where THeader : BasePageHeader, new() {
  public TokkBuffer Buffer { get; }
  public THeader Header { get; set; }

  public BasePage(TokkBuffer buffer) {
    Buffer = buffer;
    Header = new THeader {
      Buffer = buffer
    };
  }

  public void Initialize() {
    Header.Read();
  }
  
  public void Save() {
    Header.Write();
  }
  
}
