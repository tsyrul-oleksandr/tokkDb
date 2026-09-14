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

  //I-5 and DL-8: for an Array column whose elements are objects, the field that identifies an
  //element, so that a delta of the column matches its elements by that field rather than by
  //position. Declared here because a column's declaration is where the catalogue keeps what
  //it knows about a column (D-4); empty means the elements have no key and are matched by
  //position. The engine stores it and does not check it: a key the elements do not honour
  //makes the diff fall back to positions and say so (V-2).
  public string ElementKey { get; set; } = string.Empty;

  public ColumnDescriptor() { }

  public ColumnDescriptor(string name, ValueTypeEnum type, string description = "", bool unique = false,
      bool readOnly = false, IDocumentValue defaultValue = null, string semanticTypeName = "",
      IEnumerable<string> validationPatterns = null, string elementKey = "") {
    Name = name;
    Type = type;
    Description = description;
    Unique = unique;
    ReadOnly = readOnly;
    DefaultValue = defaultValue ?? new NullDocumentValue();
    SemanticTypeName = semanticTypeName ?? string.Empty;
    ValidationPatterns = validationPatterns?.ToList() ?? [];
    ElementKey = elementKey ?? string.Empty;
  }
}
