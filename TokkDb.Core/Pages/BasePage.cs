using TokkDb.Core.Pages.Fields;

namespace TokkDb.Core.Pages;

public abstract class BasePage {
  private IEnumerable<IPageField> _fields;
  public const int HeaderStartPosition = 0;
  protected virtual int BodyStartPosition => Fields.Sum(field => field.Size) + 1;
  public PageBuffer Buffer { get; set; }
  public IServiceProvider ServiceProvider { get; set; }
  public UIntPageField IndexField { get; set; } = new();
  public EnumBytePageField<PageType> PageTypeField { get; set; } = new();
  protected virtual IEnumerable<IPageField> Fields => _fields ??= GetFields();

  public BasePage() { }
  public BasePage(PageBuffer buffer) {
    Buffer = buffer;
  }

  public virtual void Initialize() {
    LoadFields();
  }

  public virtual void Save() {
    SaveFields();
  }

  protected virtual void LoadFields() {
    var position = HeaderStartPosition;
    foreach (var field in Fields) {
      field.Read(Buffer, position);
      position += field.Size;
    }
  }
  
  protected virtual void SaveFields() {
    var position = HeaderStartPosition;
    foreach (var field in Fields) {
      field.Write(Buffer, position);
      position += field.Size;
    }
  }
  
  protected virtual IEnumerable<IPageField> GetFields() {
    return [IndexField, PageTypeField];
  }
}
