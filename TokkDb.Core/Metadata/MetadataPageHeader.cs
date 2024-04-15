using TokkDb.Core.Buffer;
using TokkDb.Core.Pages.Headers;

namespace TokkDb.Core.Metadata;

public class MetadataPageHeader : BasePageHeader {
  public MetadataPageHeader() { }
  public MetadataPageHeader(TokkBuffer buffer) : base(buffer) { }
}
