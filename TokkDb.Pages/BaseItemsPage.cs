using TokkDb.Buffer;
using TokkDb.Configuration;

namespace TokkDb.Pages;

public abstract class BaseItemsPage : BasePage {
  private const byte SlotSize = 4;
  public ushort FreeBytes { get; set; } = TokkConstants.PageSize - StartContentBufferPosition;
  public ushort NextFreePosition { get; protected set; } = StartContentBufferPosition;
  public byte ItemsCount { get; protected set; }
  
  protected override int LoadHeader() {
    var position = base.LoadHeader();
    ItemsCount = Buffer.ReadByte(position, out var readBytes);
    position += readBytes;
    FreeBytes = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    NextFreePosition = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteByte(ItemsCount, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(FreeBytes, position, out writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(NextFreePosition, position, out writeBytes);
    position += writeBytes;
    return position;
  }
  
  public virtual BufferSlice GetItem(byte index) {
    var addressValue = GetItemSlotAddressValue(index);
    return Buffer.Slice(addressValue.Position, addressValue.Length);
  }

  public virtual BufferSlice RegisterItem(ushort bytesLength) {
    var newItemIndex = ItemsCount;
    var startPosition = NextFreePosition;
    SetItemSlotAddressValue(newItemIndex, startPosition, bytesLength);
    NextFreePosition += bytesLength;
    ItemsCount++;
    FreeBytes -= bytesLength;
    return Buffer.Slice(startPosition, bytesLength);
  }

  public virtual IEnumerable<BufferSlice> GetItems() {
    for (byte i = 0; i < ItemsCount; i++) {
      yield return GetItem(i);
    }
  }
  
  protected virtual (ushort Position, ushort Length) GetItemSlotAddressValue(byte index) {
    var address = GetItemSlotAddress(index);
    return (Buffer.ReadUShort(address.Position), Buffer.ReadUShort(address.Length));
  }
  
  protected virtual void SetItemSlotAddressValue(byte index, ushort position, ushort length) {
    var address = GetItemSlotAddress(index);
    Buffer.WriteUShort(position, address.Position, out _);
    Buffer.WriteUShort(length, address.Length, out _);
  }

  protected static (ushort Position, ushort Length) GetItemSlotAddress(byte index) {
    var slotLengthAddress = (ushort)(TokkConstants.PageSize - (index + 1) * SlotSize);
    var slotPositionAddress = (ushort)(slotLengthAddress + 2);
    return (slotPositionAddress, slotLengthAddress);
  }
}
