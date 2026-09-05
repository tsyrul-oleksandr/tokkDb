using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages;

//The one place that knows how a collection descriptor looks as a document. It is ordinary
//document serialization: no reader or writer here understands the catalogue as bytes.
public static class CollectionDescriptorDocument {
  public const string IdField = "id";
  public const string NameField = "name";
  public const string DescriptionField = "description";
  public const string SchemaVersionField = "schemaVersion";
  public const string ColumnsField = "columns";
  public const string OwningCollectionIdField = "owningCollectionId";
  public const string LastOwningCollectionIdField = "lastOwningCollectionId";
  public const string DataFirstPageField = "dataFirstPage";
  public const string DataLastPageField = "dataLastPage";
  public const string PrimaryIndexRootField = "primaryIndexRoot";
  public const string SecondaryIndexRootsField = "secondaryIndexRoots";
  public const string IndexNameField = "index";
  public const string IndexRootField = "root";
  public const string FreeSpaceRootField = "freeSpaceRoot";
  public const string RecordCountField = "recordCount";
  public const string HistoryCollectionIdField = "historyCollectionId";
  public const string RetentionPolicyField = "retentionPolicy";

  public const string ColumnNameField = "name";
  public const string ColumnTypeField = "type";
  public const string ColumnUniqueField = "unique";
  public const string ColumnReadOnlyField = "readOnly";
  public const string ColumnDefaultValueField = "defaultValue";
  public const string ColumnDescriptionField = "description";
  public const string ColumnSemanticTypeField = "semanticType";
  public const string ColumnValidationPatternsField = "validationPatterns";

  //The hardcoded minimal descriptor of D-4: the columns of the catalogue's own documents,
  //and the only schema in the engine that is not itself read from a document.
  public static List<ColumnDescriptor> CreateSelfColumns() {
    return [
      new ColumnDescriptor(IdField, ValueTypeEnum.Ulid, "Identifier of the collection", unique: true,
        readOnly: true),
      new ColumnDescriptor(NameField, ValueTypeEnum.String, "Name of the collection", unique: true),
      new ColumnDescriptor(DescriptionField, ValueTypeEnum.String, "What the collection holds"),
      new ColumnDescriptor(SchemaVersionField, ValueTypeEnum.UInt, "Version of the column set"),
      new ColumnDescriptor(ColumnsField, ValueTypeEnum.Array, "Column definitions of the collection"),
      new ColumnDescriptor(OwningCollectionIdField, ValueTypeEnum.UInt,
        "The number the data pages of the collection carry in their header", unique: true, readOnly: true),
      new ColumnDescriptor(LastOwningCollectionIdField, ValueTypeEnum.UInt,
        "Highest owning id ever issued; on the catalogue's own descriptor"),
      new ColumnDescriptor(DataFirstPageField, ValueTypeEnum.UInt, "First page of the data chain"),
      new ColumnDescriptor(DataLastPageField, ValueTypeEnum.UInt, "Last page of the data chain"),
      new ColumnDescriptor(PrimaryIndexRootField, ValueTypeEnum.UInt, "Root page of the primary index"),
      new ColumnDescriptor(SecondaryIndexRootsField, ValueTypeEnum.Array, "Root pages of the secondary indexes"),
      new ColumnDescriptor(FreeSpaceRootField, ValueTypeEnum.UInt, "Root page of the free space structure"),
      new ColumnDescriptor(RecordCountField, ValueTypeEnum.UInt, "Number of records in the collection"),
      new ColumnDescriptor(HistoryCollectionIdField, ValueTypeEnum.Ulid, "Reserved for versioning (D-5)"),
      new ColumnDescriptor(RetentionPolicyField, ValueTypeEnum.String, "Reserved for versioning (D-5)")
    ];
  }

  public static ObjectDocument Write(CollectionDescriptor descriptor) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(descriptor.Id));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [IdField] = new UlidDocumentValue(descriptor.Id),
      [NameField] = new StringDocumentValue(descriptor.Name),
      [DescriptionField] = new StringDocumentValue(descriptor.Description),
      [SchemaVersionField] = new UIntDocumentValue(descriptor.SchemaVersion),
      [ColumnsField] = new ArrayDocumentValue(descriptor.Columns.Select(WriteColumn).ToArray()),
      [OwningCollectionIdField] = new UIntDocumentValue(descriptor.OwningCollectionId),
      [LastOwningCollectionIdField] = new UIntDocumentValue(descriptor.LastOwningCollectionId),
      [DataFirstPageField] = new UIntDocumentValue(descriptor.DataFirstPage),
      [DataLastPageField] = new UIntDocumentValue(descriptor.DataLastPage),
      [PrimaryIndexRootField] = new UIntDocumentValue(descriptor.PrimaryIndexRoot),
      [SecondaryIndexRootsField] = new ArrayDocumentValue(descriptor.SecondaryIndexRoots
        .OrderBy(root => root.Key, StringComparer.Ordinal)
        .Select(IDocumentValue (root) => new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
          [IndexNameField] = new StringDocumentValue(root.Key),
          [IndexRootField] = new UIntDocumentValue(root.Value)
        })).ToArray()),
      [FreeSpaceRootField] = new UIntDocumentValue(descriptor.FreeSpaceRoot),
      [RecordCountField] = new UIntDocumentValue(descriptor.RecordCount),
      [HistoryCollectionIdField] = new UlidDocumentValue(descriptor.HistoryCollectionId),
      [RetentionPolicyField] = new StringDocumentValue(descriptor.RetentionPolicy)
    }));
    return document;
  }

  public static CollectionDescriptor Read(ObjectDocument document) {
    var value = (ObjectDocumentValue)document.Value;
    return new CollectionDescriptor {
      Id = ReadUlid(value, IdField),
      Name = ReadString(value, NameField),
      Description = ReadString(value, DescriptionField),
      SchemaVersion = (ushort)ReadUInt(value, SchemaVersionField),
      Columns = ReadArray(value, ColumnsField).Select(ReadColumn).ToList(),
      OwningCollectionId = ReadUInt(value, OwningCollectionIdField),
      LastOwningCollectionId = ReadUInt(value, LastOwningCollectionIdField),
      DataFirstPage = ReadUInt(value, DataFirstPageField),
      DataLastPage = ReadUInt(value, DataLastPageField),
      PrimaryIndexRoot = ReadUInt(value, PrimaryIndexRootField),
      SecondaryIndexRoots = ReadArray(value, SecondaryIndexRootsField)
        //A database written before the roots named themselves holds bare numbers here, and
        //those cannot be matched to an index, so they are passed over rather than guessed at.
        .OfType<ObjectDocumentValue>()
        .ToDictionary(root => ReadString(root, IndexNameField), root => ReadUInt(root, IndexRootField),
          StringComparer.Ordinal),
      FreeSpaceRoot = ReadUInt(value, FreeSpaceRootField),
      RecordCount = ReadUInt(value, RecordCountField),
      HistoryCollectionId = ReadUlid(value, HistoryCollectionIdField),
      RetentionPolicy = ReadString(value, RetentionPolicyField)
    };
  }

  private static IDocumentValue WriteColumn(ColumnDescriptor column) {
    return new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [ColumnNameField] = new StringDocumentValue(column.Name),
      //The name rather than the number, so renumbering the enum cannot silently retype a column.
      [ColumnTypeField] = new StringDocumentValue(column.Type.ToString()),
      [ColumnUniqueField] = new BooleanDocumentValue(column.Unique),
      [ColumnReadOnlyField] = new BooleanDocumentValue(column.ReadOnly),
      [ColumnDefaultValueField] = column.DefaultValue,
      [ColumnDescriptionField] = new StringDocumentValue(column.Description),
      [ColumnSemanticTypeField] = new StringDocumentValue(column.SemanticTypeName),
      [ColumnValidationPatternsField] = new ArrayDocumentValue(column.ValidationPatterns
        .Select(IDocumentValue (pattern) => new StringDocumentValue(pattern)).ToArray())
    });
  }

  private static ColumnDescriptor ReadColumn(IDocumentValue value) {
    var column = (ObjectDocumentValue)value;
    return new ColumnDescriptor {
      Name = ReadString(column, ColumnNameField),
      Type = Enum.Parse<ValueTypeEnum>(ReadString(column, ColumnTypeField)),
      Unique = ReadBoolean(column, ColumnUniqueField),
      ReadOnly = ReadBoolean(column, ColumnReadOnlyField),
      DefaultValue = column.Values.GetValueOrDefault(ColumnDefaultValueField) ?? new NullDocumentValue(),
      Description = ReadString(column, ColumnDescriptionField),
      //DC-7: a database written before these fields existed reads them as their defaults.
      SemanticTypeName = ReadString(column, ColumnSemanticTypeField),
      ValidationPatterns = ReadArray(column, ColumnValidationPatternsField)
        .OfType<StringDocumentValue>().Select(pattern => pattern.Value).ToList()
    };
  }

  //A field the writer of the document did not know about reads as its default, which is
  //what keeps adding a field from being a migration.
  private static string ReadString(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;
  }

  private static uint ReadUInt(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UIntDocumentValue number ? number.Value : default;
  }

  private static bool ReadBoolean(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is BooleanDocumentValue flag && flag.Value;
  }

  private static Ulid ReadUlid(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UlidDocumentValue identifier ? identifier.Value : default;
  }

  private static IDocumentValue[] ReadArray(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is ArrayDocumentValue array ? array.Values : [];
  }
}
