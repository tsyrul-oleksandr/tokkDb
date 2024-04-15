using TokkDb.Core.Buffer;

namespace TokkDb.Core.Pages.Headers;

public class BasePageHeader {
  protected static int ByteSize = 1;
  protected static int UIntSize = 4;
  protected static HeaderField IdField = new(0, UIntSize);
  protected static HeaderField TypeField = new(IdField.NextPosition, ByteSize);
  protected static HeaderField PreviousPageIdField = new(TypeField.NextPosition, UIntSize);
  protected static HeaderField NextPageIdField = new(PreviousPageIdField.NextPosition, UIntSize);
  public TokkBuffer Buffer { get; set; }
  public uint Id { get; set; }
  public byte Type { get; set; }
  public uint PreviousPageId { get; set; }
  public uint NextPageId { get; set; }

  public BasePageHeader() { }
  public BasePageHeader(TokkBuffer buffer) {
    Buffer = buffer;
  }
  
  public void Read() {
    Id = Buffer.ReadUInt(IdField.Index);
    Type = Buffer.ReadByte(TypeField.Index);
    PreviousPageId = Buffer.ReadUInt(PreviousPageIdField.Index);
    NextPageId = Buffer.ReadUInt(NextPageIdField.Index);
  }
  
  public void Write() {
    Buffer.Write(Id, IdField.Index);
    Buffer.Write(Type, TypeField.Index);
    Buffer.Write(PreviousPageId, PreviousPageIdField.Index);
    Buffer.Write(NextPageId, NextPageIdField.Index);
  }
}
