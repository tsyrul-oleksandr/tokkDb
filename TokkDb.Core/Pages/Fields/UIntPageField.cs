namespace TokkDb.Core.Pages.Fields;

public class UIntPageField : BasePageField<uint> {
  public UIntPageField() : base(TypesConstants.UIntByteSize) { }
  public override void Read(PageBuffer buffer, int position) {
    Value = buffer.ReadUInt(position, out _);
  }

  public override void Write(PageBuffer buffer, int position) {
    buffer.WriteUInt(Value, position, out _);
  }
}
