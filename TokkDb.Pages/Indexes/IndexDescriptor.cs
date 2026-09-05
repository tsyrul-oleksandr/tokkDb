using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages.Indexes;

//One secondary index as the catalogue records it. D-4: it is a document in the _indexes
//system collection, so adding a field to it is not a migration. The root page it starts at
//is not here — that is a physical pointer and lives in the collection's own catalogue
//document (D-2), beside the data chain and the free-space root.
public class IndexDescriptor {
  public Ulid Id { get; set; }

  //One index per column, so the column names it. A composite-column index would need a name
  //of its own; nothing in DC-4's list of indexed fields asks for one.
  public string Name => ColumnName;

  public string CollectionName { get; set; } = string.Empty;
  public string ColumnName { get; set; } = string.Empty;

  //DC-4: a unique index refuses a second record carrying a value it already holds.
  public bool Unique { get; set; }
}

public static class IndexDescriptorDocument {
  public const string IdField = "id";
  public const string CollectionField = "collection";
  public const string ColumnField = "column";
  public const string UniqueField = "unique";

  public static List<ColumnDescriptor> CreateColumns() {
    return [
      new ColumnDescriptor(IdField, ValueTypeEnum.Ulid, "Identifier of the index", unique: true,
        readOnly: true),
      new ColumnDescriptor(CollectionField, ValueTypeEnum.String, "Collection the index covers"),
      new ColumnDescriptor(ColumnField, ValueTypeEnum.String, "Column the index is keyed by"),
      new ColumnDescriptor(UniqueField, ValueTypeEnum.Boolean, "Whether a repeated value is refused")
    ];
  }

  public static ObjectDocument Write(IndexDescriptor descriptor) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(descriptor.Id));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [IdField] = new UlidDocumentValue(descriptor.Id),
      [CollectionField] = new StringDocumentValue(descriptor.CollectionName),
      [ColumnField] = new StringDocumentValue(descriptor.ColumnName),
      [UniqueField] = new BooleanDocumentValue(descriptor.Unique)
    }));
    return document;
  }

  public static IndexDescriptor Read(ObjectDocument document) {
    var value = (ObjectDocumentValue)document.Value;
    return new IndexDescriptor {
      Id = value.Values.GetValueOrDefault(IdField) is UlidDocumentValue id ? id.Value : default,
      CollectionName = ReadString(value, CollectionField),
      ColumnName = ReadString(value, ColumnField),
      Unique = value.Values.GetValueOrDefault(UniqueField) is BooleanDocumentValue flag && flag.Value
    };
  }

  private static string ReadString(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;
  }
}
