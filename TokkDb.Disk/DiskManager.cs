using TokkDb.Core;
using TokkDb.Core.Pages;

namespace TokkDb.Disk;

public class DiskManager {
  public DiskReader Reader { get; set; }
  public DiskWriter Writer { get; set; }
  
  public DiskManager(DiskReader reader, DiskWriter writer) {
    Reader = reader;
    Writer = writer;
  }
  
  public PageBuffer CreateNewPage(uint index) {
    return new PageBuffer(new byte[TokkConstants.PageSize]) {
      Index = index
    };
  }

  public bool IsBlank() {
    return Reader.IsBlank();
  }

  public PageBuffer ReadPage(uint index) {
    return Reader.ReadPage(index);
  }

  public void WritePages(params PageBuffer[] pages) {
    Writer.WritePages(pages);
  }
}
