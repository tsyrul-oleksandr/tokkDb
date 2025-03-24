using TokkDb.Configuration;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

public class MetadataPageManager {
  private readonly PageManager _pageManager;
  private readonly TransactionManager _transactionManager;
  private MetadataPage _metadataPage;
  
  public MetadataPageManager(PageManager pageManager, TransactionManager transactionManager) {
    _pageManager = pageManager;
    _transactionManager = transactionManager;
  }

  public void Initialize() {
    if (_pageManager.IsBlank()) {
      InitializeNewMetadataPage();
    }
    _metadataPage = _pageManager.LoadPage<MetadataPage>(TokkConstants.MetadataPageIndex);
  }

  public bool IsExist(string entityName) {
    var entity = FindEntity(entityName);
    return entity != null;
  }
  
  public void CreateEntity(string name) {
    _metadataPage.Entities.Add(name, new MetadataEntity(default, default));
    _metadataPage.EntitiesCount = (byte)_metadataPage.Entities.Count;
    _transactionManager.Track(_metadataPage);
  }
  
  public uint GetFirstPageIndex(string name) {
    var entity = GetEntity(name);
    return entity.DataFirstPageId;
  }
  
  public uint GetLastPageIndex(string name) {
    var entity = GetEntity(name);
    return entity.DataLastPageId;
  }
  
  public void SetFirstPageIndex(string name, uint pageIndex) {
    var entity = GetEntity(name);
    entity.DataFirstPageId = pageIndex;
    _transactionManager.Track(_metadataPage);
  }
  
  public void SetLastPageIndex(string name, uint pageIndex) {
    var entity = GetEntity(name);
    entity.DataLastPageId = pageIndex;
    if (entity.DataFirstPageId == default) {
      entity.DataFirstPageId = pageIndex;
    }
    _transactionManager.Track(_metadataPage);
  }
  
  public uint GetNewPageIndex() {
    _metadataPage.LastPageId++;
    return _metadataPage.LastPageId;
  }

  protected virtual MetadataEntity GetEntity(string name) {
    return FindEntity(name) ?? throw new EntityNotFoundException($"Entity {name} not found");
  }
  
  protected virtual MetadataEntity FindEntity(string name) {
    return _metadataPage.Entities.GetValueOrDefault(name);
  }
  
  protected virtual void InitializeNewMetadataPage() {
    var metadataPage = _pageManager.CreateNewMemoryPage<MetadataPage>(PageType.Metadata, TokkConstants.MetadataPageIndex);
    metadataPage.CreatedAt = DateTime.UtcNow;
    _pageManager.SavePages(metadataPage);
  }
}
