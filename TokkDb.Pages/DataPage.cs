namespace TokkDb.Pages;

public class DataPage : BaseItemsPage {
  public uint NextPageIndex { get; set; }
  public override PageType Type { get; set; } = PageType.Data;

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    NextPageIndex = Buffer.ReadUInt(position, out var readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUInt(NextPageIndex, position, out var writeBytes);
    position += writeBytes;
    return position;
  }
}
