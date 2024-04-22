using Microsoft.Extensions.DependencyInjection;
using TokkDb.Core;
using TokkDb.Core.Pages;
using TokkDb.Data.Collections;
using TokkDb.Data.Documents.Serializers;
using TokkDb.Data.Pages;
using TokkDb.Disk;

namespace TokkDb.Pages;

public class PageManager {
  public DiskManager DiskManager { get; }
  public IServiceProvider ServiceProvider { get; }
  public MetadataPage MetadataPage { get; private set; }

  public PageManager(DiskManager diskManager, IServiceProvider serviceProvider) {
    DiskManager = diskManager;
    ServiceProvider = serviceProvider;
  }
  
  public virtual void Load() {
    if (DiskManager.IsBlank()) {
      InitializeDatabase();
    }
    MetadataPage = LoadPage<MetadataPage>(TokkConstants.MetadataPageIndex);
  }
  
  public ITokkCollection<T> GetCollection<T>(string name) where T : class, new() {
    if (!MetadataPage.TryGetCollectionIndex(name, out var collectionIndex)) {
      var newCollectionPage = CreateNewPage<CollectionPage>(PageType.Collection);
      collectionIndex = newCollectionPage.IndexField.Value;
      MetadataPage.AddCollectionIndex(name, collectionIndex);
      SavePages(newCollectionPage, MetadataPage);
    }
    var collectionPage = LoadPage<CollectionPage>(collectionIndex);
    var serializer = ServiceProvider.GetService<DocumentSerializer<T>>();
    return new TokkCollection<T>(collectionPage, DiskManager, serializer);
  }

  protected virtual T CreateNewPage<T>(PageType type, uint? index = null) where T : BasePage, new() {
    index ??= GetNewPageIndex();
    var buffer = DiskManager.CreateNewPage(index.Value);
    var newPage = new T {
      Buffer = buffer,
      ServiceProvider = ServiceProvider,
      IndexField = {
        Value = index.Value
      },
      PageTypeField = {
        Value = type
      }
    };
    return newPage;
  }

  protected virtual uint GetNewPageIndex() {
    MetadataPage.LastPageId.Value++;
    return MetadataPage.LastPageId.Value;
  }

  protected virtual T LoadPage<T>(uint index) where T : BasePage, new() {
    var buffer = DiskManager.ReadPage(index);
    var newPage = new T {
      Buffer = buffer,
      ServiceProvider = ServiceProvider
    };
    newPage.Initialize();
    return newPage;
  }
  
  protected virtual void SavePage<T>(T page) where T : BasePage, new() {
    page.Save();
    DiskManager.WritePages(page.Buffer);
  }
  
  protected virtual void SavePages(params BasePage[] pages) {
    foreach (var page in pages) {
      page.Save();
    }
    DiskManager.WritePages(pages.Select(page => page.Buffer).ToArray());
  }
  
  protected virtual void InitializeDatabase() {
    var metadataPage = CreateNewPage<MetadataPage>(PageType.Header, TokkConstants.MetadataPageIndex);
    metadataPage.CreatedAt.Value = DateTime.UtcNow;
    SavePage(metadataPage);
  }
}
