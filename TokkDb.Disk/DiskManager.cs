using TokkDb.Buffer;

namespace TokkDb.Disk;

public class DiskManager {
  public DiskReader Reader { get; set; }
  public DiskWriter Writer { get; set; }
  
  public DiskManager(string filePath) {
    Reader = new DiskReader(filePath);
    Writer = new DiskWriter(filePath);
  }

  public bool IsBlank() {
    return Reader.IsBlank();
  }

  public PageBuffer ReadPage(uint index) {
    return Reader.ReadPage(index);
  }

  public void WritePage(PageBuffer page) {
    Writer.WritePage(page);
  }
}
