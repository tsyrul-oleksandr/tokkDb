using TokkDb.Core;
using TokkDb.Core.Metadata;
using TokkDb.Core.Pages;

namespace TokkDb.Disk;

public class DiskManager {
  public MetadataPage MetadataPage { get; }

  public DiskManager(MetadataPage metadataPage) {
    MetadataPage = metadataPage;
  }

  public PageBuffer CreateNewPage() {
    return new PageBuffer(new byte[TokkConstants.PageSize]) {
      Index = uint.MaxValue
    };
  }
}
