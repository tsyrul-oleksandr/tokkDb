using TokkDb.Buffer;

namespace TokkDb.Pages;

public abstract class BaseItemsPage : BasePage {
  private const byte SlotSize = 4;

  //A slot whose position is zero is free. No item can live at zero: the content area starts
  //after the header, so the value is unambiguous and costs no extra byte.
  private const ushort FreeSlotPosition = 0;

  private ushort? _freeBytes;

  //A fresh page has its whole content area free, but the page size is known only once the
  //page belongs to a file, so this cannot be a field initializer. The control area at the
  //end of the page is not part of it.
  public ushort FreeBytes {
    get => _freeBytes ??= (ushort)(PageSize - StartContentBufferPosition - ControlAreaByteSize);
    set => _freeBytes = value;
  }

  //Bytes sitting in slots that were freed. They are handed out again to an item that fits
  //one of them; making them contiguous with FreeBytes is what compaction (ST-4) is for.
  public ushort FreeListBytes { get; set; }

  public ushort NextFreePosition { get; protected set; } = StartContentBufferPosition;
  public ushort ItemsCount { get; protected set; }

  //Everything the page could give up: the contiguous tail plus what sits in freed slots.
  //Compaction is what turns the second into the first.
  public ushort ReclaimableBytes => (ushort)(FreeBytes + FreeListBytes);

  //Whether anything is still stored here at all.
  public bool IsEmpty {
    get {
      for (ushort i = 0; i < ItemsCount; i++) {
        if (!IsItemFree(i)) {
          return false;
        }
      }
      return true;
    }
  }

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    ItemsCount = Buffer.ReadUShort(position, out var readBytes);
    position += readBytes;
    FreeBytes = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    NextFreePosition = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    FreeListBytes = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUShort(ItemsCount, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(FreeBytes, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(NextFreePosition, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(FreeListBytes, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  public virtual BufferSlice GetItem(ushort index) {
    var addressValue = GetItemSlotAddressValue(index);
    return Buffer.Slice(addressValue.Position, addressValue.Length);
  }

  public virtual bool IsItemFree(ushort index) {
    return GetItemSlotAddressValue(index).Position == FreeSlotPosition;
  }

  //An item costs its own bytes plus the slot it takes from the directory growing down from
  //the control area — unless a slot with no bytes of its own can hold it, which costs only
  //the bytes.
  public virtual bool CanFit(ushort bytesLength) {
    return FreeBytes >= bytesLength + SlotSize
      || (FreeBytes >= bytesLength && FindEmptySlot() is not null);
  }

  public virtual BufferSlice RegisterItem(ushort bytesLength) {
    return RegisterItem(bytesLength, out _);
  }

  //Hands back the slot the item went into as well as its bytes.
  //
  //Which slot that is has to come from here rather than be worked out again by the caller.
  //There are three ways an item can be placed and a caller that models only some of them
  //writes the item correctly and records the wrong address for it — which a scan cannot see,
  //because the record is on the page, and an index cannot survive, because the entry points
  //at a slot that was never written (D-2).
  public virtual BufferSlice RegisterItem(ushort bytesLength, out ushort slotIndex) {
    //A slot with no bytes of its own — freed, or emptied by compaction — keeps its index and
    //is given space out of the contiguous run. That costs no new slot, so the directory does
    //not grow for ever on a page that is written and freed repeatedly.
    if (FreeBytes >= bytesLength && FindEmptySlot() is { } emptied) {
      var reusedPosition = NextFreePosition;
      SetItemSlotAddressValue(emptied, reusedPosition, bytesLength);
      NextFreePosition += bytesLength;
      FreeBytes -= bytesLength;
      slotIndex = emptied;
      return Buffer.Slice(reusedPosition, bytesLength);
    }
    if (FreeBytes < bytesLength + SlotSize) {
      throw new PageOverflowException(
        $"Item of {bytesLength} bytes does not fit into page {Index} with {FreeBytes} free bytes.");
    }
    var newItemIndex = ItemsCount;
    var startPosition = NextFreePosition;
    SetItemSlotAddressValue(newItemIndex, startPosition, bytesLength);
    NextFreePosition += bytesLength;
    ItemsCount++;
    FreeBytes -= (ushort)(bytesLength + SlotSize);
    slotIndex = newItemIndex;
    return Buffer.Slice(startPosition, bytesLength);
  }

  //Returns an item's space to the page's free list. The slot stays — its identity is what an
  //index entry points at (D-2) — and its bytes are counted as reclaimable, to be recovered by
  //the next compaction.
  //
  //The bytes are not held for the slot to hand out again. Doing that would mean remembering
  //where they were, and a slot has room for a position or the marker that says it is free,
  //not both: what the code did instead was reconstruct the position by summing the lengths of
  //the slots before it, which is only right while every item lies in slot order. One reused
  //slot breaks that, and the reconstruction then points at a live record, which the next write
  //overwrites. Space is reclaimed by compaction, which is what ST-4 is for.
  public virtual void FreeItem(ushort index) {
    var address = GetItemSlotAddressValue(index);
    if (address.Position == FreeSlotPosition) {
      return;
    }
    SetItemSlotAddressValue(index, FreeSlotPosition, 0);
    FreeListBytes += address.Length;
  }

  //ST-4. Slides the live records down over the gaps the freed ones left, so that scattered
  //free bytes become one usable run. Slot indexes do not change: the slot array is the
  //indirection (D-2), so a record moving inside its page is invisible from outside it.
  //
  //The records are moved in the order they lie in the page, which is not the order of their
  //slots. A slot handed out again — a freed one reused, or one compaction emptied and
  //RegisterItem then gave space out of the run — holds bytes further along the page than a
  //slot after it, so walking the directory in order and sliding each record down would copy
  //one record over another that had not been moved yet. Ascending by position, every
  //destination is at or below its source and everything below it is already in place, so the
  //overlapping copies are safe.
  public virtual void Compact() {
    var live = new List<(ushort Slot, ushort Position, ushort Length)>();
    for (ushort i = 0; i < ItemsCount; i++) {
      var address = GetItemSlotAddressValue(i);
      if (address.Position == FreeSlotPosition) {
        //The slot stays — its identity is stable — but its bytes go back to the run.
        SetItemSlotAddressValue(i, FreeSlotPosition, 0);
        continue;
      }
      live.Add((i, address.Position, address.Length));
    }
    live.Sort((left, right) => left.Position.CompareTo(right.Position));

    var position = StartContentBufferPosition;
    foreach (var item in live) {
      if (item.Position != position) {
        Buffer.MoveBytes(item.Position, position, item.Length);
        SetItemSlotAddressValue(item.Slot, position, item.Length);
      }
      position += item.Length;
    }
    NextFreePosition = position;
    FreeListBytes = 0;
    FreeBytes = (ushort)(PageSize - ControlAreaByteSize - ItemsCount * SlotSize - position);
  }

  public virtual IEnumerable<BufferSlice> GetItems() {
    for (ushort i = 0; i < ItemsCount; i++) {
      if (IsItemFree(i)) {
        continue;
      }
      yield return GetItem(i);
    }
  }

  //The slots holding an item, with their indexes, for callers that have to address one.
  public virtual IEnumerable<(ushort Index, BufferSlice Buffer)> GetItemSlots() {
    for (ushort i = 0; i < ItemsCount; i++) {
      if (IsItemFree(i)) {
        continue;
      }
      yield return (i, GetItem(i));
    }
  }

  //First fit. What is left over inside an oversized slot stays there until compaction; a
  //free-space map across pages is ST-1's job, not this one's.
  //A slot that was freed and then emptied by compaction: it has an index but no space.
  protected virtual ushort? FindEmptySlot() {
    for (ushort i = 0; i < ItemsCount; i++) {
      var address = GetItemSlotAddressValue(i);
      if (address.Position == FreeSlotPosition && address.Length == 0) {
        return i;
      }
    }
    return null;
  }

  protected virtual (ushort Position, ushort Length) GetItemSlotAddressValue(ushort index) {
    var address = GetItemSlotAddress(index);
    return (Buffer.ReadUShort(address.Position), Buffer.ReadUShort(address.Length));
  }

  protected virtual void SetItemSlotAddressValue(ushort index, ushort position, ushort length) {
    var address = GetItemSlotAddress(index);
    Buffer.WriteUShort(position, address.Position, out _);
    Buffer.WriteUShort(length, address.Length, out _);
  }

  //The slot directory grows down from the control area, not from the end of the page.
  protected virtual (ushort Position, ushort Length) GetItemSlotAddress(ushort index) {
    var slotLengthAddress = (ushort)(PageSize - ControlAreaByteSize - (index + 1) * SlotSize);
    var slotPositionAddress = (ushort)(slotLengthAddress + 2);
    return (slotPositionAddress, slotLengthAddress);
  }
}
