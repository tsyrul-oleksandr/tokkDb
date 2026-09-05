using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages.Relations;

//A referential constraint as the catalogue records it: a column of one collection must hold
//a value that some record of another collection carries in a column of its own.
//
//DC-4: the check this describes is only affordable because the target column is indexed.
//Without one, every write of a source record would scan the whole target collection, so the
//index is part of the relation rather than an optimisation of it — which is why creating a
//relation creates the index it needs.
public class RelationDescriptor {
  public Ulid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string SourceCollection { get; set; } = string.Empty;
  public string SourceColumn { get; set; } = string.Empty;
  public string TargetCollection { get; set; } = string.Empty;
  public string TargetColumn { get; set; } = string.Empty;
}

public static class RelationDescriptorDocument {
  public const string IdField = "id";
  public const string NameField = "name";
  public const string SourceCollectionField = "sourceCollection";
  public const string SourceColumnField = "sourceColumn";
  public const string TargetCollectionField = "targetCollection";
  public const string TargetColumnField = "targetColumn";

  public static List<ColumnDescriptor> CreateColumns() {
    return [
      new ColumnDescriptor(IdField, ValueTypeEnum.Ulid, "Identifier of the relation", unique: true,
        readOnly: true),
      new ColumnDescriptor(NameField, ValueTypeEnum.String, "Name of the relation", unique: true),
      new ColumnDescriptor(SourceCollectionField, ValueTypeEnum.String, "Collection the constraint is on"),
      new ColumnDescriptor(SourceColumnField, ValueTypeEnum.String, "Column that must refer to something"),
      new ColumnDescriptor(TargetCollectionField, ValueTypeEnum.String, "Collection referred to"),
      new ColumnDescriptor(TargetColumnField, ValueTypeEnum.String, "Indexed column referred to")
    ];
  }

  public static ObjectDocument Write(RelationDescriptor descriptor) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(descriptor.Id));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [IdField] = new UlidDocumentValue(descriptor.Id),
      [NameField] = new StringDocumentValue(descriptor.Name),
      [SourceCollectionField] = new StringDocumentValue(descriptor.SourceCollection),
      [SourceColumnField] = new StringDocumentValue(descriptor.SourceColumn),
      [TargetCollectionField] = new StringDocumentValue(descriptor.TargetCollection),
      [TargetColumnField] = new StringDocumentValue(descriptor.TargetColumn)
    }));
    return document;
  }

  public static RelationDescriptor Read(ObjectDocument document) {
    var value = (ObjectDocumentValue)document.Value;
    return new RelationDescriptor {
      Id = value.Values.GetValueOrDefault(IdField) is UlidDocumentValue id ? id.Value : default,
      Name = ReadString(value, NameField),
      SourceCollection = ReadString(value, SourceCollectionField),
      SourceColumn = ReadString(value, SourceColumnField),
      TargetCollection = ReadString(value, TargetCollectionField),
      TargetColumn = ReadString(value, TargetColumnField)
    };
  }

  private static string ReadString(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;
  }
}
