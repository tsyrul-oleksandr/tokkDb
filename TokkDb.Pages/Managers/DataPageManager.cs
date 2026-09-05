using TokkDb.Buffer;
using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Pages.Indexes;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

public class DataPageManager {
  private readonly PageManager _pageManager;
  private readonly CollectionCatalog _catalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly TransactionManager _transactionManager;

  //One tree per collection. A tree holds no state of its own — it reads its root out of the
  //catalogue document every time (D-2) — so these are cached to keep the split counters and
  //not because rebuilding one would cost anything.
  private readonly Dictionary<string, BPlusTree> _primaryIndexes = new(StringComparer.Ordinal);

  public DataPageManager(PageManager pageManager, CollectionCatalog catalog, FreeSpaceManager freeSpace,
      TransactionManager transactionManager) {
    _pageManager = pageManager;
    _catalog = catalog;
    _freeSpace = freeSpace;
    _transactionManager = transactionManager;
  }

  //What a record can occupy on a data page: the content area of an empty page, less the slot
  //it needs. Anything larger goes to an overflow chain (ST-5).
  public int MaxInPageRecordLength =>
    _pageManager.PageSize - BasePage.StartContentBufferPosition - BasePage.ControlAreaByteSize - SlotByteSize;

  //What stays on the data page of a record that overflowed: its header, so that a scan can
  //read the flags and the identifier without following the chain, and the pointer to it.
  public const int OverflowPrefixByteSize = TypesConstants.UIntByteSize + TypesConstants.IntByteSize;

  //DC-4: the collection's primary index, keyed by the record identity of D-1 and holding the
  //(pageId, slotId) of D-2.
  public BPlusTree PrimaryIndex(string collectionName) {
    if (!_primaryIndexes.TryGetValue(collectionName, out var tree)) {
      tree = new BPlusTree(_pageManager, _catalog, _freeSpace, _transactionManager, collectionName);
      _primaryIndexes[collectionName] = tree;
    }
    return tree;
  }

  //Forgets the cached trees, as the free-space structures are forgotten, because the
  //catalogue they read their roots from has been reloaded.
  public void Reset() {
    _primaryIndexes.Clear();
  }

  public BufferSlice Register(string collectionName, ushort bytesLength) {
    return RegisterRow(collectionName, bytesLength).Buffer;
  }

  //The write path for a whole record image. Records that fit go on a data page as they are;
  //records that do not keep their header on the page and put the body in an overflow chain.
  public DataRow WriteRecord(string collectionName, byte[] recordBytes) {
    EnsurePrimaryIndex(collectionName);
    var row = recordBytes.Length <= MaxInPageRecordLength
      ? WriteInPageRecord(collectionName, recordBytes)
      : WriteOverflowRecord(collectionName, recordBytes);
    //Upsert rather than insert, because this is the write half of the copy on write of
    //VR-12 as well as the write of a new record: the entry either does not exist yet or has
    //to follow the record to where its new image went.
    IndexRow(collectionName, StoredRecordUtilities.ReadHeaderFrom(recordBytes).RecordId, row.Address);
    return row;
  }

  private DataRow WriteInPageRecord(string collectionName, byte[] recordBytes) {
    var row = RegisterRow(collectionName, (ushort)recordBytes.Length);
    row.Buffer.WriteBytes(recordBytes, 0, out _);
    return row;
  }

  //The read path. A record that fits its page is handed back where it lies; one that
  //overflowed is put together again out of its chain.
  public BufferSlice ReadRecordBuffer(DataRow row) {
    var header = StoredRecordUtilities.ReadHeader(row.Buffer);
    if (!header.Flags.HasFlag(RecordFlags.HasOverflow)) {
      return row.Buffer;
    }
    var firstOverflowPage = row.Buffer.ReadUInt(RecordHeader.ByteSize, out var readBytes);
    var bodyLength = row.Buffer.ReadInt(RecordHeader.ByteSize + readBytes, out _);

    var assembled = new byte[RecordHeader.ByteSize + bodyLength];
    Array.Copy(row.Buffer.ReadBytes(RecordHeader.ByteSize, 0, out _), assembled, RecordHeader.ByteSize);
    var written = RecordHeader.ByteSize;
    var next = firstOverflowPage;
    while (next != default) {
      var page = LoadOverflowPage(next);
      page.CopyPayloadTo(assembled, written);
      written += page.PayloadLength;
      next = page.NextPageIndex;
    }
    return new BufferSlice(assembled);
  }

  private DataRow WriteOverflowRecord(string collectionName, byte[] recordBytes) {
    var bodyLength = recordBytes.Length - RecordHeader.ByteSize;
    //The chain is built first, so the pointer written on the data page is already good.
    var firstOverflowPage = WriteOverflowChain(collectionName, recordBytes, RecordHeader.ByteSize, bodyLength);

    var row = RegisterRow(collectionName, (ushort)(RecordHeader.ByteSize + OverflowPrefixByteSize));
    var header = StoredRecordUtilities.ReadHeaderFrom(recordBytes);
    header.Flags |= RecordFlags.HasOverflow;
    StoredRecordUtilities.WriteHeader(header, row.Buffer);
    row.Buffer.WriteUInt(firstOverflowPage, RecordHeader.ByteSize, out var writeBytes);
    row.Buffer.WriteInt(bodyLength, RecordHeader.ByteSize + writeBytes, out _);
    return row;
  }

  private uint WriteOverflowChain(string collectionName, byte[] source, int offset, int length) {
    uint firstPageIndex = default;
    OverflowPage previous = null;
    var written = 0;
    while (written < length) {
      var page = AllocateOverflowPage(collectionName);
      var take = Math.Min(page.Capacity, length - written);
      page.SetPayload(source, offset + written, take);
      written += take;
      _transactionManager.Track(page);
      if (previous is null) {
        firstPageIndex = page.Index;
      } else {
        previous.NextPageIndex = page.Index;
      }
      previous = page;
    }
    return firstPageIndex;
  }

  private OverflowPage AllocateOverflowPage(string collectionName) {
    //A chain freed earlier is used again before the file is grown.
    if (_freeSpace.TakeFreeOverflowPage(collectionName) is { } reused) {
      var recycled = LoadOverflowPage(reused);
      recycled.NextPageIndex = default;
      recycled.PayloadLength = 0;
      return recycled;
    }
    var pageIndex = _catalog.AllocatePageIndex();
    var page = _pageManager.CreateNewMemoryPage<OverflowPage>(PageType.Overflow, pageIndex);
    page.OwningCollectionId = _catalog.GetOwningCollectionId(collectionName);
    _freeSpace.RecordOverflowPage(collectionName, pageIndex, inUse: true);
    return page;
  }

  private void FreeOverflowChain(string collectionName, uint firstPageIndex) {
    var next = firstPageIndex;
    while (next != default) {
      var page = LoadOverflowPage(next);
      next = page.NextPageIndex;
      page.NextPageIndex = default;
      page.PayloadLength = 0;
      _transactionManager.Track(page);
      _freeSpace.RecordOverflowPage(collectionName, page.Index, inUse: false);
    }
  }

  private OverflowPage LoadOverflowPage(uint pageIndex) {
    return _transactionManager.FindTrackedPage<OverflowPage>(pageIndex)
      ?? _pageManager.LoadPage<OverflowPage>(pageIndex);
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

  //The primary lookup. One descent of the tree — a handful of pages whatever the collection
  //holds — instead of the walk of every data page this used to be.
  public DataRow? FindLiveRow(string collectionName, Ulid recordId) {
    if (!HasPrimaryIndex(collectionName)) {
      return ScanForLiveRow(collectionName, recordId);
    }
    if (PrimaryIndex(collectionName).Find(PrimaryIndexKey(recordId)) is not { } address) {
      return null;
    }
    var page = LoadPage(address.PageIndex);
    //The entry points at a live image or it should not be there, but the slot is checked
    //rather than trusted: an entry left behind by something that failed to remove it would
    //otherwise hand back whatever now occupies the slot.
    if (page.IsItemFree(address.SlotIndex)) {
      return null;
    }
    var row = new DataRow(address, page.GetItem(address.SlotIndex));
    return StoredRecordUtilities.ReadHeader(row.Buffer).IsLive ? row : null;
  }

  //What the lookup was before the index, and what it still is for a collection that has no
  //tree: the system collections, whose catalogue is what a tree would have to read its own
  //root out of, and a collection written before the index existed and not yet written to
  //since.
  private DataRow? ScanForLiveRow(string collectionName, Ulid recordId) {
    foreach (var row in GetAllRows(collectionName)) {
      //Only the header is needed to recognise the record, and the header is always on the
      //page even when the body is not.
      var header = StoredRecordUtilities.ReadHeader(row.Buffer);
      if (header.RecordId == recordId && header.IsLive) {
        return row;
      }
    }
    return null;
  }

  //D-3 encodes the identity; the tree compares bytes and never learns what a Ulid is.
  private static byte[] PrimaryIndexKey(Ulid recordId) {
    return KeyEncoder.Encode(recordId).Bytes;
  }

  //The catalogue's own collections are not indexed in this pass. A tree reads its root out
  //of the catalogue document, and _collections has to be readable before any document can
  //be read — page 0 keeps a CollectionsPrimaryIndexRoot for when that circle is closed.
  private bool IsIndexed(string collectionName) {
    return !_catalog.Get(collectionName).IsSystem;
  }

  private bool HasPrimaryIndex(string collectionName) {
    return IsIndexed(collectionName) && !PrimaryIndex(collectionName).IsEmpty;
  }

  private void IndexRow(string collectionName, Ulid recordId, DocumentAddress address) {
    if (IsIndexed(collectionName)) {
      PrimaryIndex(collectionName).Upsert(PrimaryIndexKey(recordId), address);
    }
  }

  //A collection written before the primary index existed has records and no tree, and an
  //index holding only what has been written since would answer for the rest with a confident
  //null. It is built once, from the scan the lookup used to be, before the first write that
  //would make it incomplete.
  private void EnsurePrimaryIndex(string collectionName) {
    if (!IsIndexed(collectionName)) {
      return;
    }
    var tree = PrimaryIndex(collectionName);
    if (!tree.IsEmpty || _catalog.Get(collectionName).RecordCount == 0) {
      return;
    }
    foreach (var row in GetAllRows(collectionName)) {
      var header = StoredRecordUtilities.ReadHeader(row.Buffer);
      if (header.IsLive) {
        tree.Insert(PrimaryIndexKey(header.RecordId), row.Address);
      }
    }
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
    var slot = page.GetItem(address.SlotIndex);
    var header = StoredRecordUtilities.ReadHeader(slot);
    //A record that overflowed takes its chain with it, through this same one seam.
    if (header.Flags.HasFlag(RecordFlags.HasOverflow)) {
      FreeOverflowChain(collectionName, slot.ReadUInt(RecordHeader.ByteSize, out _));
    }
    header.Flags = flags;
    StoredRecordUtilities.WriteHeader(header, slot);
    //A deleted record leaves the index; a superseded one keeps its entry, because the write
    //that supersedes it repoints that same entry at the new image. Both happen inside the
    //one transaction of VR-12, so nothing ever reads the entry in between.
    if (flags == RecordFlags.Deleted && IsIndexed(collectionName)) {
      PrimaryIndex(collectionName).Delete(PrimaryIndexKey(header.RecordId));
    }
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
