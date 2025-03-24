namespace TokkDb.Pages;

public class MetadataPage : BasePage {
  public override PageType Type { get; set; } = PageType.Metadata;
  public uint LastPageId { get; set; }
  public DateTime CreatedAt { get; set; }
  public byte EntitiesCount { get; set; }
  public Dictionary<string, MetadataEntity> Entities { get; set; } = [];

  public override void Load() {
    base.Load();
    LoadEntities();
  }

  public override void Save() {
    base.Save();
    SaveEntities();
  }

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    LastPageId = Buffer.ReadUInt(position, out var readBytes);
    position += readBytes;
    CreatedAt = Buffer.ReadDateTime(position, out readBytes);
    position += readBytes;
    EntitiesCount = Buffer.ReadByte(position, out readBytes);
    position += readBytes;
    return position;
  }
  
  private int LoadEntities() {
    int position = StartContentBufferPosition;
    Entities = [];
    for (var i = 0; i < EntitiesCount; i++) {
      var key = Buffer.ReadString(position, out var readBytes);
      position += readBytes;
      var firstPageId = Buffer.ReadUInt(position, out readBytes);
      position += readBytes;
      var lastPageId = Buffer.ReadUInt(position, out readBytes);
      position += readBytes;
      Entities.Add(key, new MetadataEntity(firstPageId, lastPageId));
    }
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUInt(LastPageId, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteDateTime(CreatedAt, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteByte(EntitiesCount, position, out writeBytes);
    position += writeBytes;
    return position;
  }
  
  private int SaveEntities() {
    int position = StartContentBufferPosition;
    foreach (var (key, value) in Entities) {
      Buffer.WriteString(key, position, out var writeBytes);
      position += writeBytes;
      Buffer.WriteUInt(value.DataFirstPageId, position, out writeBytes);
      position += writeBytes;
      Buffer.WriteUInt(value.DataLastPageId, position, out writeBytes);
      position += writeBytes;
    }
    return position;
  }
}
