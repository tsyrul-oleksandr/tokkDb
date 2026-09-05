using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb.Pages;

//ST-1 and D-6. The explicit free-space structure: for each collection, which of its pages
//have room and how much, so that finding somewhere to put a record is a lookup instead of a
//walk down the whole page chain.
//
//The structure of a collection is rooted at the freeSpaceRoot of its catalogue document and
//cached in memory from first use, so the lookup costs no page read.
public class FreeSpaceManager {
  private readonly PageManager _pageManager;
  private readonly RootPageManager _rootPageManager;
  private readonly CollectionCatalog _catalog;
  private readonly TransactionManager _transactionManager;

  //collection name -> its pages, in the order the structure holds them.
  private readonly Dictionary<string, List<FreeSpaceEntry>> _entries = new(StringComparer.Ordinal);

  public FreeSpaceManager(PageManager pageManager, RootPageManager rootPageManager, CollectionCatalog catalog,
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _rootPageManager = rootPageManager;
    _catalog = catalog;
    _transactionManager = transactionManager;
  }

  //Forgets the cached structures. The catalogue is reloaded at open and these go with it.
  public void Reset() {
    _entries.Clear();
  }

  public IReadOnlyList<FreeSpaceEntry> GetEntries(string collectionName) {
    return Load(collectionName);
  }

  //The pages worth trying for a record of this size, best filled first so that the file is
  //packed rather than spread.
  public IEnumerable<uint> FindPagesWithRoom(string collectionName, ushort bytesLength) {
    return Load(collectionName)
      .Where(entry => entry.CanHoldRecords && entry.ReclaimableBytes > 0 && entry.ReclaimableBytes >= bytesLength)
      .OrderBy(entry => entry.ReclaimableBytes)
      .Select(entry => entry.PageIndex)
      .ToList();
  }

  public void Record(string collectionName, uint pageIndex, ushort reclaimableBytes, BlockState state) {
    _transactionManager.RequireTransaction();
    var entries = Load(collectionName);
    //The structure has to exist before it can be written to, and creating it adds an entry of
    //its own, so it comes first.
    EnsureStructureExists(collectionName, entries);
    var index = entries.FindIndex(entry => entry.PageIndex == pageIndex);
    if (index >= 0) {
      entries[index] = new FreeSpaceEntry(pageIndex, reclaimableBytes, state);
    } else {
      entries.Add(new FreeSpaceEntry(pageIndex, reclaimableBytes, state));
    }
    Save(collectionName, entries);
  }

  //Overflow pages belong to one record and never hold another, so they are kept out of the
  //allocator: in use they are Reserved, and free they are Free with nothing reclaimable,
  //which is how a freed chain is told apart from a data page with room.
  public void RecordOverflowPage(string collectionName, uint pageIndex, bool inUse) {
    Record(collectionName, pageIndex, 0, inUse ? BlockState.Reserved : BlockState.Free);
  }

  public uint? TakeFreeOverflowPage(string collectionName) {
    var entries = Load(collectionName);
    var index = entries.FindIndex(entry => entry.State == BlockState.Free && entry.ReclaimableBytes == 0);
    if (index < 0) {
      return null;
    }
    var pageIndex = entries[index].PageIndex;
    RecordOverflowPage(collectionName, pageIndex, inUse: true);
    return pageIndex;
  }

  //A page whose checksum did not verify is never handed out again.
  public void MarkDamaged(string collectionName, uint pageIndex) {
    Record(collectionName, pageIndex, 0, BlockState.Damaged);
  }

  private List<FreeSpaceEntry> Load(string collectionName) {
    if (_entries.TryGetValue(collectionName, out var cached)) {
      return cached;
    }
    var entries = new List<FreeSpaceEntry>();
    var next = _catalog.Get(collectionName).FreeSpaceRoot;
    while (next != default) {
      var page = LoadStructurePage(next);
      entries.AddRange(page.Entries);
      next = page.NextPageIndex;
    }
    _entries[collectionName] = entries;
    return entries;
  }

  //Spreads the entries over the structure's pages, extending the chain when they outgrow it
  //and emptying any page they no longer reach.
  private void Save(string collectionName, List<FreeSpaceEntry> entries) {
    var pageIndex = _catalog.Get(collectionName).FreeSpaceRoot;
    var written = 0;
    FreeSpacePage previous = null;
    while (pageIndex != default || written < entries.Count) {
      var page = pageIndex != default
        ? LoadStructurePage(pageIndex)
        : AppendStructurePage(collectionName, previous);
      var take = Math.Min(page.Capacity, entries.Count - written);
      page.Entries = take > 0 ? entries.GetRange(written, take) : [];
      written += take;
      _transactionManager.Track(page);
      previous = page;
      pageIndex = page.NextPageIndex;
    }
  }

  private void EnsureStructureExists(string collectionName, List<FreeSpaceEntry> entries) {
    if (_catalog.Get(collectionName).FreeSpaceRoot != default) {
      return;
    }
    var pageIndex = _rootPageManager.AllocatePageIndex();
    var page = _pageManager.CreateNewMemoryPage<FreeSpacePage>(PageType.FreeSpace, pageIndex);
    page.OwningCollectionId = _catalog.GetOwningCollectionId(collectionName);
    _transactionManager.Track(page);
    _catalog.SetFreeSpaceRoot(collectionName, pageIndex);
    //The structure's own pages hold no records and are never handed out for one.
    entries.Add(new FreeSpaceEntry(pageIndex, 0, BlockState.Reserved));
  }

  private FreeSpacePage AppendStructurePage(string collectionName, FreeSpacePage previous) {
    var pageIndex = _rootPageManager.AllocatePageIndex();
    var page = _pageManager.CreateNewMemoryPage<FreeSpacePage>(PageType.FreeSpace, pageIndex);
    page.OwningCollectionId = _catalog.GetOwningCollectionId(collectionName);
    previous.NextPageIndex = pageIndex;
    _transactionManager.Track(previous);
    return page;
  }

  private FreeSpacePage LoadStructurePage(uint pageIndex) {
    return _transactionManager.FindTrackedPage<FreeSpacePage>(pageIndex)
      ?? _pageManager.LoadPage<FreeSpacePage>(pageIndex);
  }
}
