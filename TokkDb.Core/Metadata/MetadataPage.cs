using TokkDb.Core.Buffer;
using TokkDb.Core.Pages;

namespace TokkDb.Core.Metadata;

public class MetadataPage : BasePage<MetadataPageHeader> {

  public MetadataPage(TokkBuffer buffer) : base(buffer) { }
}
