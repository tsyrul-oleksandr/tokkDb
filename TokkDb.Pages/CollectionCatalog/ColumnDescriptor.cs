using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages;

public class ColumnDescriptor {
  public string Name { get; set; } = string.Empty;
  public ValueTypeEnum Type { get; set; } = ValueTypeEnum.Null;
  public bool Unique { get; set; }
  public bool ReadOnly { get; set; }

  //Any document value, so a default needs no encoding of its own.
  public IDocumentValue DefaultValue { get; set; } = new NullDocumentValue();
  public string Description { get; set; } = string.Empty;

  public ColumnDescriptor() { }

  public ColumnDescriptor(string name, ValueTypeEnum type, string description = "", bool unique = false,
      bool readOnly = false, IDocumentValue defaultValue = null) {
    Name = name;
    Type = type;
    Description = description;
    Unique = unique;
    ReadOnly = readOnly;
    DefaultValue = defaultValue ?? new NullDocumentValue();
  }
}
