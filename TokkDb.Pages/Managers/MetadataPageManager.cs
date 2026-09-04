using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

public class MetadataPageManager {
  private readonly PageManager _pageManager;
  private readonly RootPageManager _rootPageManager;
  private readonly TransactionManager _transactionManager;
  private MetadataPage _metadataPage;
  
  public MetadataPageManager(PageManager pageManager, RootPageManager rootPageManager,
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _rootPageManager = rootPageManager;
    _transactionManager = transactionManager;
  }

  //The root page has to be initialized first: it says where the catalogue lives.
  public void Initialize() {
    if (_rootPageManager.CollectionsFirstPageId == default) {
      InitializeNewMetadataPage();
      return;
    }
    _metadataPage = _pageManager.LoadPage<MetadataPage>(_rootPageManager.CollectionsFirstPageId);
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
    return _rootPageManager.AllocatePageIndex();
  }

  protected virtual MetadataEntity GetEntity(string name) {
    return FindEntity(name) ?? throw new EntityNotFoundException($"Entity {name} not found");
  }
  
  protected virtual MetadataEntity FindEntity(string name) {
    return _metadataPage.Entities.GetValueOrDefault(name);
  }
  
  protected virtual void InitializeNewMetadataPage() {
    var pageIndex = _rootPageManager.AllocatePageIndex();
    _metadataPage = _pageManager.CreateNewMemoryPage<MetadataPage>(PageType.Metadata, pageIndex);
    _rootPageManager.SetCollectionsFirstPageId(pageIndex);
    _transactionManager.Track(_metadataPage);
  }
}
