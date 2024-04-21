namespace TokkDb.Core.Pages.Fields;

public class LongPageField : BasePageField<long> {

  public LongPageField() : base(TypesConstants.LongByteSize) { }
  public override void Read(PageBuffer buffer, int position) {
    Value = buffer.ReadLong(position, out _);
  }

  public override void Write(PageBuffer buffer, int position) {
    buffer.WriteLong(Value, position, out _);
  }
}
