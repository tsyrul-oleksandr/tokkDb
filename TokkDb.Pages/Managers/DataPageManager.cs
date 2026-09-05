using TokkDb.Buffer;
using TokkDb.Documents;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

public class DataPageManager {
  private readonly PageManager _pageManager;
  private readonly CollectionCatalog _catalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly TransactionManager _transactionManager;

  public DataPageManager(PageManager pageManager, CollectionCatalog catalog, FreeSpaceManager freeSpace,
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _catalog = catalog;
    _freeSpace = freeSpace;
    _transactionManager = transactionManager;
  }

  public BufferSlice Register(string collectionName, ushort bytesLength) {
    return RegisterRow(collectionName, bytesLength).Buffer;
  }

  public DataRow RegisterRow(string collectionName, ushort bytesLength) {
    var page = GetAvailablePage(collectionName, bytesLength);
    _transactionManager.Track(page);
    var slotIndex = FindSlotFor(page, bytesLength);
    var buffer = page.RegisterItem(bytesLength);
    _catalog.IncrementRecordCount(collectionName);
    RecordFreeSpace(collectionName, page);
    return new DataRow(new DocumentAddress(page.Index, slotIndex), buffer);
  }

  //RegisterItem may reuse a freed slot rather than append one, so where the record landed
  //has to be worked out from the same rule rather than assumed to be the end.
  private static ushort FindSlotFor(DataPage page, ushort bytesLength) {
    var before = page.ItemsCount;
    for (ushort i = 0; i < before; i++) {
      if (page.IsItemFree(i) && page.WouldReuseSlot(i, bytesLength)) {
        return i;
      }
    }
    return before;
  }

  //Rewrites a record where it already lies. Nothing here grows a record: an update that
  //needs more room than its slot has waits for ST-6.
  public void UpdateRow(DocumentAddress address, RecordHeader header, ObjectDocument document) {
    var page = LoadPage(address.PageIndex);
    var slot = page.GetItem(address.SlotIndex);
    var length = StoredRecordUtilities.GetBytesLength(header, document);
    if (length > slot.Length) {
      throw new PageOverflowException(
        $"A record of {length} bytes does not fit the {slot.Length} byte slot {address.SlotIndex} " +
        $"it occupies on page {address.PageIndex}.");
    }
    StoredRecordUtilities.ToBuffer(header, document, slot);
    _transactionManager.Track(page);
  }

  public IEnumerable<BufferSlice> GetAll(string collectionName) {
    return GetAllRows(collectionName).Select(row => row.Buffer);
  }

  public IEnumerable<DataRow> GetAllRows(string collectionName) {
    foreach (var page in GetPages(collectionName)) {
      //Freed slots are skipped: their bytes belong to the free list, not to any record.
      foreach (var (slotIndex, buffer) in page.GetItemSlots()) {
        yield return new DataRow(new DocumentAddress(page.Index, slotIndex), buffer);
      }
    }
  }

  //The primary lookup, such as it is before Phase 5 puts an index behind it: a scan that
  //reads each record header and stops at the live image of the wanted record.
  public DataRow? FindLiveRow(string collectionName, Ulid recordId) {
    foreach (var row in GetAllRows(collectionName)) {
      var header = StoredRecordUtilities.ReadHeader(row.Buffer);
      if (header.RecordId == recordId && header.IsLive) {
        return row;
      }
    }
    return null;
  }

  //The one mechanism that takes a record image out of use. It is called from exactly one
  //place — the RemoveCurrentVersion seam of VR-12 — and nothing else in the engine frees or
  //retires an image.
  public void RetireRow(string collectionName, DocumentAddress address, RecordFlags flags,
      RetentionPolicy retentionPolicy) {
    if (retentionPolicy != RetentionPolicy.None) {
      throw new NotSupportedException(
        $"{nameof(RetentionPolicy)}.{retentionPolicy} is not implemented in this pass (D-5). " +
        $"Only {nameof(RetentionPolicy)}.{nameof(RetentionPolicy.None)} retires an image.");
    }
    var page = LoadPage(address.PageIndex);
    //The image is marked before it goes, so that keeping it instead becomes a matter of not
    //freeing the slot rather than of writing something different.
    var header = StoredRecordUtilities.ReadHeader(page.GetItem(address.SlotIndex));
    header.Flags = flags;
    StoredRecordUtilities.WriteHeader(header, page.GetItem(address.SlotIndex));
    page.FreeItem(address.SlotIndex);
    _transactionManager.Track(page);
    _catalog.DecrementRecordCount(collectionName);
    RecordFreeSpace(collectionName, page);
  }

  private void RecordFreeSpace(string collectionName, DataPage page) {
    //A page holding nothing is Free and can take anything; one still holding records is
    //Occupied and offers whatever it has left.
    var state = page.IsEmpty ? BlockState.Free : BlockState.Occupied;
    _freeSpace.Record(collectionName, page.Index, page.ReclaimableBytes, state);
  }

  //ST-1. The free-space structure says which pages are worth trying, so this no longer walks
  //the whole chain for every insert.
  private DataPage GetAvailablePage(string collectionName, ushort bytesLength) {
    foreach (var pageIndex in _freeSpace.FindPagesWithRoom(collectionName, bytesLength)) {
      DataPage page;
      try {
        page = LoadPage(pageIndex);
      } catch (PageCorruptedException) {
        //A damaged page is recorded as such and never offered again.
        _freeSpace.MarkDamaged(collectionName, pageIndex);
        continue;
      }
      if (page.CanFit(bytesLength)) {
        return page;
      }
      //The room is there but scattered. ST-4: close the gaps and try again.
      if (page.ReclaimableBytes >= bytesLength + SlotByteSize) {
        page.Compact();
        _transactionManager.Track(page);
        if (page.CanFit(bytesLength)) {
          return page;
        }
      }
    }
    return CreateNewPage(collectionName);
  }

  //What a slot costs in the directory, for deciding whether compaction would free enough.
  private const ushort SlotByteSize = 4;

  protected virtual IEnumerable<DataPage> GetPages(string collectionName) {
    var nextPageIndex = _catalog.GetDataFirstPage(collectionName);
    while (nextPageIndex != default) {
      var page = LoadPage(nextPageIndex);
      yield return page;
      nextPageIndex = page.NextPageIndex;
    }
  }

  //A page already changed in this transaction must be handed back as the same object, or the
  //second reader would work from a stale copy and one of the two sets of changes would be lost.
  protected virtual DataPage LoadPage(uint pageIndex) {
    return _transactionManager.FindTrackedPage<DataPage>(pageIndex) ?? _pageManager.LoadPage<DataPage>(pageIndex);
  }

  protected virtual DataPage CreateNewPage(string collectionName) {
    var newPageIndex = _catalog.AllocatePageIndex();
    var newPage = _pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, newPageIndex);
    newPage.OwningCollectionId = _catalog.GetOwningCollectionId(collectionName);
    var lastPageIndex = _catalog.GetDataLastPage(collectionName);
    if (lastPageIndex != default) {
      var previousLastPage = LoadPage(lastPageIndex);
      previousLastPage.NextPageIndex = newPageIndex;
      _transactionManager.Track(previousLastPage);
    }
    _transactionManager.Track(newPage);
    //Last, so that the catalogue write that follows sees a page chain that is already whole.
    _catalog.SetDataLastPage(collectionName, newPageIndex);
    RecordFreeSpace(collectionName, newPage);
    return newPage;
  }
}
