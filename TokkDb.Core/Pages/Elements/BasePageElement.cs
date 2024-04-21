using TokkDb.Core.Buffer;

namespace TokkDb.Core.Pages.Elements;

public abstract class BasePageElement : IPageElement {
  public IBufferAddress Address { get; set; }
}
