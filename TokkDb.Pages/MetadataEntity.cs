namespace TokkDb.Pages;

public class MetadataEntity {
  public uint DataFirstPageId { get; set; }
  public uint DataLastPageId { get; set; }

  public MetadataEntity(uint dataFirstPageId, uint dataLastPageId) {
    DataFirstPageId = dataFirstPageId;
    DataLastPageId = dataLastPageId;
  }
}
