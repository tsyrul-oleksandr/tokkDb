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
  //the control area — unless a freed slot already big enough can take it.
  public virtual bool CanFit(ushort bytesLength) {
    return FreeBytes >= bytesLength + SlotSize || FindFreeSlot(bytesLength) is not null;
  }

  public virtual BufferSlice RegisterItem(ushort bytesLength) {
    //A freed slot is used before the page is grown, which is what returning space to the
    //free list is for.
    if (FindFreeSlot(bytesLength) is { } reused) {
      return ReuseSlot(reused, bytesLength);
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
    return Buffer.Slice(startPosition, bytesLength);
  }

  //Returns an item's space to the page's free list. The slot keeps its size so the space can
  //be handed out again; the bytes themselves are left alone until something reuses them.
  public virtual void FreeItem(ushort index) {
    var address = GetItemSlotAddressValue(index);
    if (address.Position == FreeSlotPosition) {
      return;
    }
    SetItemSlotAddressValue(index, FreeSlotPosition, address.Length);
    FreeListBytes += address.Length;
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
  protected virtual ushort? FindFreeSlot(ushort bytesLength) {
    for (ushort i = 0; i < ItemsCount; i++) {
      var address = GetItemSlotAddressValue(i);
      if (address.Position == FreeSlotPosition && address.Length >= bytesLength) {
        return i;
      }
    }
    return null;
  }

  protected virtual BufferSlice ReuseSlot(ushort index, ushort bytesLength) {
    var address = GetItemSlotAddressValue(index);
    var startPosition = FindSlotContentPosition(index, address.Length);
    SetItemSlotAddressValue(index, startPosition, address.Length);
    FreeListBytes -= address.Length;
    return Buffer.Slice(startPosition, address.Length);
  }

  //A freed slot keeps its length but loses its position, so the position is recovered from
  //where the item before it ends.
  protected virtual ushort FindSlotContentPosition(ushort index, ushort length) {
    var position = StartContentBufferPosition;
    for (ushort i = 0; i < index; i++) {
      position += GetItemSlotAddressValue(i).Length;
    }
    return position;
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
