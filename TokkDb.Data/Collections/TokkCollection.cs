using TokkDb.Data.Documents.Serializers;
using TokkDb.Data.Pages;
using TokkDb.Disk;

namespace TokkDb.Data.Collections;

public class TokkCollection<T> : ITokkCollection<T> where T : class, new() {
  public CollectionPage CollectionPage { get; }
  public DiskManager DiskManager { get; }
  public DocumentSerializer<T> Serializer { get; }

  public TokkCollection(CollectionPage collectionPage, DiskManager diskManager, DocumentSerializer<T> serializer) {
    CollectionPage = collectionPage;
    DiskManager = diskManager;
    Serializer = serializer;
  }

  public void Insert(T entity) {
    var document = Serializer.Serialize(entity);
    
  }
}
