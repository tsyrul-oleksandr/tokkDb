namespace TokkDb.Core.Pages.Headers;

public class PageHeader {
  public static HeaderField IndexField = new(0, TypesConstants.UIntByteSize);
  public static HeaderField TypeField = new(IndexField.NextPosition, TypesConstants.ByteByteSize);
  public static HeaderField PreviousPageIdField = new(TypeField.NextPosition, TypesConstants.UIntByteSize);
  public static HeaderField NextPageIdField = new(PreviousPageIdField.NextPosition, TypesConstants.UIntByteSize);
  public PageBuffer Buffer { get; set; }
  public uint Index { get; set; }
  public byte Type { get; set; }
  public uint PreviousPageId { get; set; }
  public uint NextPageId { get; set; }

  public PageHeader() { }
  public PageHeader(PageBuffer buffer) {
    Buffer = buffer;
  }
  
  /*public void Read() {
    Id = Buffer.ReadUInt(IdField.Index);
    Type = Buffer.ReadByte(TypeField.Index);
    PreviousPageId = Buffer.ReadUInt(PreviousPageIdField.Index);
    NextPageId = Buffer.ReadUInt(NextPageIdField.Index);
  }
  
  public void Write() {
    Buffer.Write(Id, IdField.Index);
    Buffer.WriteByte(Type, TypeField.Index);
    Buffer.Write(PreviousPageId, PreviousPageIdField.Index);
    Buffer.Write(NextPageId, NextPageIdField.Index);
  }*/
}
