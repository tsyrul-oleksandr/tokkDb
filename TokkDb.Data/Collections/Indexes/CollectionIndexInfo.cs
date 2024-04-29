using TokkDb.Core.Pages.Fields;

namespace TokkDb.Data.Collections.Indexes;

public class CollectionIndex {
  private IEnumerable<IPageField> _fields;
  protected virtual IEnumerable<IPageField> Fields => _fields ??= GetFields();
  public StringPageField Name { get; set; }
  public object Left { get; set; }
  public object Right { get; set; }

  protected virtual IEnumerable<IPageField> GetFields() {
    return [];
  }
}
