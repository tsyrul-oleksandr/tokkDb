namespace TokkDb.Core.Pages.Fields;

public class StringPageField : BasePageField<string> {
  
  public override int Read(PageBuffer buffer, int position) {
    buffer.ReadString(position, out var size);
    return size;
  }

  public override int Write(PageBuffer buffer, int position) {
    throw new NotImplementedException();
  }
}
