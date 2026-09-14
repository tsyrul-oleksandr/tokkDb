using TokkDb.Pages.Indexes;

namespace TokkDb.Pages.Versions;

//V-5 and HS-4. Where a history collection's version index starts: a field of the history
//collection's own catalogue document (D-2), like every other physical pointer.
public sealed class VersionIndexRoot : IndexRoot {
  private readonly CollectionCatalog _catalog;
  private readonly string _historyCollectionName;

  public VersionIndexRoot(CollectionCatalog catalog, string historyCollectionName) {
    _catalog = catalog;
    _historyCollectionName = historyCollectionName;
  }

  public override string Name => "versions";

  public override uint Read() {
    return _catalog.Get(_historyCollectionName).VersionIndexRoot;
  }

  public override void Write(uint pageIndex) {
    _catalog.SetVersionIndexRoot(_historyCollectionName, pageIndex);
  }
}
