namespace TokkDb.Core.Pages.Fields;

public class DateTimePageField : BasePageField<DateTime> {

  public DateTimePageField() : base(TypesConstants.DateTimeByteSize) { }
  public override void Read(PageBuffer buffer, int position) {
    Value = buffer.ReadDateTime(position, out _);
  }

  public override void Write(PageBuffer buffer, int position) {
    buffer.WriteDateTime(Value, position, out _);
  }
}
