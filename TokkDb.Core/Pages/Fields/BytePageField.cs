namespace TokkDb.Core.Pages.Fields;

public class BytePageField : BasePageField<byte> {

  public BytePageField() : base(TypesConstants.ByteByteSize) { }
  public override void Read(PageBuffer buffer, int position) {
    buffer.WriteByte(Value, position);
  }

  public override void Write(PageBuffer buffer, int position) {
    Value = buffer.ReadByte(position);
  }
}
