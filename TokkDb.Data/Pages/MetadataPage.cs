using TokkDb.Core.Buffer;
using TokkDb.Core.Pages;
using TokkDb.Core.Pages.Fields;
using TokkDb.Data.Documents.Buffer;

namespace TokkDb.Data.Pages;

public class MetadataPage : BasePage {
  protected const int BodyEndPosition = 8000;
  public UIntPageField LastPageId { get; set; } = new();
  public DateTimePageField CreatedAt { get; set; } = new();
  public BytePageField CollectionCount { get; set; } = new();
  public Dictionary<string, uint> Body { get; set; } = new();

  public MetadataPage() { }
  public MetadataPage(PageBuffer buffer) : base(buffer) { }

  public override void Initialize() {
    base.Initialize();
    LoadBody();
  }

  public override void Save() {
    base.Save();
    SaveBody();
  }

  public bool TryGetCollectionIndex(string name, out uint index) {
    return Body.TryGetValue(name, out index);
  }
  
  public virtual void AddCollectionIndex(string name, uint index) {
    Body[name] = index;
  }

  protected override void SaveFields() {
    CollectionCount.Value = (byte)Body.Count;
    base.SaveFields();
  }

  protected virtual void LoadBody() {
    Body.Clear();
    var position = BodyStartPosition;
    for (var i = 0; i < CollectionCount.Value; i++) {
      var key = Buffer.ReadString(position, out var keyBytes);
      position += keyBytes;
      var value = Buffer.ReadUInt(position, out var valueBytes);
      position += valueBytes;
      Body.Add(key, value);
    }
  }

  protected virtual void SaveBody() {
    var position = BodyStartPosition;
    foreach (var (key, value) in Body) {
      Buffer.WriteString(key, position, out var keyBytes);
      position += keyBytes;
      Buffer.WriteUInt(value, position, out var valueBytes);
      position += valueBytes;
    }
  }
  
  protected override IEnumerable<IPageField> GetFields() {
    return base.GetFields().Concat(new IPageField[] { LastPageId, CreatedAt, CollectionCount });
  }
}
