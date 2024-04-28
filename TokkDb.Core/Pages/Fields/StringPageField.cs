namespace TokkDb.Core.Pages.Fields;

public class StringPageField : BasePageField<string> {

  public override int Size { get; }

  public StringPageField(int size) : base(size) { }
  public override void Read(PageBuffer buffer, int position) {
    throw new NotImplementedException();
  }

  public override void Write(PageBuffer buffer, int position) {
    throw new NotImplementedException();
  }
}
