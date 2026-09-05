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

  //Declared by whatever owns the schema and stored uninterpreted, the way Description is. The
  //engine has no notion of a semantic type or a validation pattern — it neither normalises
  //nor checks against them — but a column that lost them on a round trip would come back as a
  //different column than the one that was declared.
  public string SemanticTypeName { get; set; } = string.Empty;
  public List<string> ValidationPatterns { get; set; } = [];

  public ColumnDescriptor() { }

  public ColumnDescriptor(string name, ValueTypeEnum type, string description = "", bool unique = false,
      bool readOnly = false, IDocumentValue defaultValue = null, string semanticTypeName = "",
      IEnumerable<string> validationPatterns = null) {
    Name = name;
    Type = type;
    Description = description;
    Unique = unique;
    ReadOnly = readOnly;
    DefaultValue = defaultValue ?? new NullDocumentValue();
    SemanticTypeName = semanticTypeName ?? string.Empty;
    ValidationPatterns = validationPatterns?.ToList() ?? [];
  }
}
