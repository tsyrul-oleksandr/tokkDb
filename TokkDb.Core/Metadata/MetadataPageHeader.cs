using TokkDb.Core.Buffer;
using TokkDb.Core.Pages.Headers;

namespace TokkDb.Core.Metadata;

public class MetadataPageHeader : PageHeader {
  public MetadataPageHeader() { }
  public MetadataPageHeader(BufferSlice buffer) : base(buffer) { }
}
