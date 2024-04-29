namespace TokkDb.Core.Pages.Fields;

public class LongPageField : BasePageField<long> {
  
  public override int Read(PageBuffer buffer, int position) {
    Value = buffer.ReadLong(position, out var size);
    return size;
  }

  public override int Write(PageBuffer buffer, int position) {
    buffer.WriteLong(Value, position, out var size);
    return size;
  }
}
