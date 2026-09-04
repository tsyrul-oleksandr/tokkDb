namespace TokkDb.Pages;

//The collections catalogue as it stands before D-4 turns it into documents. Creation time
//and page allocation now belong to the root page.
public class MetadataPage : BasePage {
  public override PageType Type { get; set; } = PageType.Metadata;
  public byte EntitiesCount { get; set; }
  public Dictionary<string, MetadataEntity> Entities { get; set; } = [];

  protected override void LoadContent() {
    LoadEntities();
  }

  protected override void SaveContent() {
    SaveEntities();
  }

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    EntitiesCount = Buffer.ReadByte(position, out var readBytes);
    position += readBytes;
    return position;
  }
  
  private int LoadEntities() {
    int position = StartContentBufferPosition;
    Entities = [];
    for (var i = 0; i < EntitiesCount; i++) {
      var key = Buffer.ReadString(position, out var readBytes);
      position += readBytes;
      var id = Buffer.ReadUInt(position, out readBytes);
      position += readBytes;
      var firstPageId = Buffer.ReadUInt(position, out readBytes);
      position += readBytes;
      var lastPageId = Buffer.ReadUInt(position, out readBytes);
      position += readBytes;
      Entities.Add(key, new MetadataEntity(id, firstPageId, lastPageId));
    }
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteByte(EntitiesCount, position, out var writeBytes);
    position += writeBytes;
    return position;
  }
  
  private int SaveEntities() {
    int position = StartContentBufferPosition;
    foreach (var (key, value) in Entities) {
      Buffer.WriteString(key, position, out var writeBytes);
      position += writeBytes;
      Buffer.WriteUInt(value.Id, position, out writeBytes);
      position += writeBytes;
      Buffer.WriteUInt(value.DataFirstPageId, position, out writeBytes);
      position += writeBytes;
      Buffer.WriteUInt(value.DataLastPageId, position, out writeBytes);
      position += writeBytes;
    }
    return position;
  }
}
