namespace TokkDb.Pages;

public class MetadataEntity {
  //The identifier every data page of this collection carries in its header.
  public uint Id { get; set; }
  public uint DataFirstPageId { get; set; }
  public uint DataLastPageId { get; set; }

  public MetadataEntity(uint id, uint dataFirstPageId, uint dataLastPageId) {
    Id = id;
    DataFirstPageId = dataFirstPageId;
    DataLastPageId = dataLastPageId;
  }
}
