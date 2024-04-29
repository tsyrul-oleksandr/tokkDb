namespace TokkDb.Core.Pages.Fields;

public class UIntPageField : BasePageField<uint> {
  public override int Read(PageBuffer buffer, int position) {
    Value = buffer.ReadUInt(position, out var size);
    return size;
  }

  public override int Write(PageBuffer buffer, int position) {
    buffer.WriteUInt(Value, position, out var size);
    return size;
  }
}
