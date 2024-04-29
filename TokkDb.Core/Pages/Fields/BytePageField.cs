namespace TokkDb.Core.Pages.Fields;

public class BytePageField : BasePageField<byte> {
  public override int Read(PageBuffer buffer, int position) {
    buffer.WriteByte(Value, position);
    return TypesConstants.ByteByteSize;
  }

  public override int Write(PageBuffer buffer, int position) {
    Value = buffer.ReadByte(position);
    return TypesConstants.ByteByteSize;
  }
}
