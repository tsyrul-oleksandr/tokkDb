using TokkDb.Core.Buffer;

namespace TokkDb.Core.Pages.Elements;

public interface IPageElement {
  IBufferAddress Address { get; set; }
}
