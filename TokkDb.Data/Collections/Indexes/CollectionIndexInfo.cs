using TokkDb.Core.Pages.Fields;

namespace TokkDb.Data.Collections.Indexes;

public class CollectionIndex {
  private IEnumerable<IPageField> _fields;
  protected virtual IEnumerable<IPageField> Fields => _fields ??= GetFields();
  public StringF

  protected virtual IEnumerable<IPageField> GetFields() {
    return [];
  }
}
