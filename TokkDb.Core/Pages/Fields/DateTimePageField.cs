namespace TokkDb.Core.Pages.Fields;

public class DateTimePageField : BasePageField<DateTime> {
  public override int Read(PageBuffer buffer, int position) {
    Value = buffer.ReadDateTime(position, out var size);
    return size;
  }

  public override int Write(PageBuffer buffer, int position) {
    buffer.WriteDateTime(Value, position, out var size);
    return size;
  }
}
