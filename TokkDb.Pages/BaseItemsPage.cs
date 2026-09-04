using TokkDb.Buffer;

namespace TokkDb.Pages;

public abstract class BaseItemsPage : BasePage {
  private const byte SlotSize = 4;
  private ushort? _freeBytes;

  //A fresh page has its whole content area free, but the page size is known only once the
  //page belongs to a file, so this cannot be a field initializer. The control area at the
  //end of the page is not part of it.
  public ushort FreeBytes {
    get => _freeBytes ??= (ushort)(PageSize - StartContentBufferPosition - ControlAreaByteSize);
    set => _freeBytes = value;
  }
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
    return position;
  }
  
  public virtual BufferSlice GetItem(ushort index) {
    var addressValue = GetItemSlotAddressValue(index);
    return Buffer.Slice(addressValue.Position, addressValue.Length);
  }

  //An item costs its own bytes plus the slot it takes from the directory growing down from the page end.
  public virtual bool CanFit(ushort bytesLength) {
    return FreeBytes >= bytesLength + SlotSize;
  }

  public virtual BufferSlice RegisterItem(ushort bytesLength) {
    if (!CanFit(bytesLength)) {
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

  public virtual IEnumerable<BufferSlice> GetItems() {
    for (ushort i = 0; i < ItemsCount; i++) {
      yield return GetItem(i);
    }
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
