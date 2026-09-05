using TokkDb.Buffer;

namespace TokkDb.Pages;

//ST-5. One link of the chain holding the body of a record too large for a page. It belongs
//to exactly one record, so it has no slot directory: the whole content area is payload.
public class OverflowPage : BasePage {
  public override PageType Type { get; set; } = PageType.Overflow;

  public uint NextPageIndex { get; set; }
  public ushort PayloadLength { get; set; }

  public int Capacity => PageSize - StartContentBufferPosition - ControlAreaByteSize;

  protected override int LoadHeader() {
    var position = base.LoadHeader();
    NextPageIndex = Buffer.ReadUInt(position, out var readBytes);
    position += readBytes;
    PayloadLength = Buffer.ReadUShort(position, out readBytes);
    position += readBytes;
    return position;
  }

  protected override int SaveHeader() {
    var position = base.SaveHeader();
    Buffer.WriteUInt(NextPageIndex, position, out var writeBytes);
    position += writeBytes;
    Buffer.WriteUShort(PayloadLength, position, out writeBytes);
    position += writeBytes;
    return position;
  }

  public void SetPayload(byte[] source, int sourceIndex, int length) {
    Buffer.WriteBytes(source.AsSpan(sourceIndex, length).ToArray(), StartContentBufferPosition, out _);
    PayloadLength = (ushort)length;
  }

  public void CopyPayloadTo(byte[] destination, int destinationIndex) {
    var payload = Buffer.ReadBytes(PayloadLength, StartContentBufferPosition, out _);
    Array.Copy(payload, 0, destination, destinationIndex, PayloadLength);
  }
}
