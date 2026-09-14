using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages.Versions;

//HS-5 and HS-6. The one place that knows how a history collection's documents look, in the
//style of CollectionDescriptorDocument: ordinary document serialization, no bytes of its own.
//
//A history collection holds four kinds of document, told apart by a type field: version
//nodes, operations, and (step 2.6) schema and relation nodes. Field names are one letter,
//because a node is written for every write of every versioned record and the names are paid
//for on each; the catalogue declares what each one means (DC-7).
public static class HistoryDocuments {
  public const string TypeField = "t";
  public const int NodeType = 1;
  public const int OperationType = 2;
  public const int SchemaType = 3;
  public const int RelationType = 4;

  //Node fields.
  public const string KindField = "k";
  public const string ParentField = "p";
  public const string OperationField = "o";
  public const string SchemaVersionField = "s";
  public const string DistanceField = "d";
  public const string DeltaField = "x";
  public const string ImageField = "i";
  public const string ImageSchemaVersionField = "v";
  public const string ReplacedHeadField = "r";
  public const string CutFromField = "c";

  //Operation fields.
  public const string RecordedAtField = "w";
  public const string AuthorField = "a";
  public const string CauseField = "u";
  public const string CommentField = "m";

  //Schema and relation node fields (V-11). A schema node reuses "s" for its version.
  public const string ColumnsField = "y";
  public const string MigrationsField = "g";
  public const string ColumnsKnownField = "n";
  public const string UnknownField = "e";
  public const string RelationField = "l";

  public static List<ColumnDescriptor> Columns() {
    return [
      new ColumnDescriptor(TypeField, ValueTypeEnum.Int,
        "What the document is: 1 a version node, 2 an operation, 3 a schema node, 4 a relation node"),
      new ColumnDescriptor(KindField, ValueTypeEnum.String,
        "Node: Baseline, Insert, Update, Delete or Restore (HS-5)"),
      new ColumnDescriptor(ParentField, ValueTypeEnum.Ulid, "Node: the parent version; absent for a root"),
      new ColumnDescriptor(OperationField, ValueTypeEnum.Ulid, "Node: the operation that wrote it (V-7)"),
      new ColumnDescriptor(SchemaVersionField, ValueTypeEnum.UInt,
        "Node: the schema version its delta was computed under"),
      new ColumnDescriptor(DistanceField, ValueTypeEnum.Int, "Node: distance from the nearest keyframe ancestor"),
      new ColumnDescriptor(DeltaField, ValueTypeEnum.Array,
        "Node: the delta from the parent; absent for a root, a tombstone and a keyframe (V-1)"),
      new ColumnDescriptor(ImageField, ValueTypeEnum.Object,
        "Node: the full image of a keyframe that has left the head"),
      new ColumnDescriptor(ImageSchemaVersionField, ValueTypeEnum.UInt, "Node: the schema version of the image"),
      new ColumnDescriptor(ReplacedHeadField, ValueTypeEnum.Ulid, "Node: for a restore, the head it replaced"),
      new ColumnDescriptor(CutFromField, ValueTypeEnum.Ulid, "Node: for a purge root, the parent the purge removed"),
      new ColumnDescriptor(RecordedAtField, ValueTypeEnum.DateTime,
        "Operation: the wall clock when it recorded its first version (V-8)"),
      new ColumnDescriptor(AuthorField, ValueTypeEnum.String, "Operation: who made the change (V-12)"),
      new ColumnDescriptor(CauseField, ValueTypeEnum.Ulid,
        "Operation: the request or event that caused it; default when unknown"),
      new ColumnDescriptor(CommentField, ValueTypeEnum.String, "Operation: caller text, never record values"),
      new ColumnDescriptor(ColumnsField, ValueTypeEnum.Array, "Schema node: the column declarations of the version (V-11)"),
      new ColumnDescriptor(MigrationsField, ValueTypeEnum.Array, "Schema node: the migration steps that produced the version"),
      new ColumnDescriptor(ColumnsKnownField, ValueTypeEnum.Boolean,
        "Schema node: false when only the step is known, as at switch-on"),
      new ColumnDescriptor(UnknownField, ValueTypeEnum.Boolean,
        "Schema node: earlier declarations unknown; relation node: creation time unknown (switch-on)"),
      new ColumnDescriptor(RelationField, ValueTypeEnum.Object, "Relation node: the relation as declared (V-11)")
    ];
  }

  public static ObjectDocument WriteSchemaNode(SchemaNode node) {
    return Document(node.Id, new Dictionary<string, IDocumentValue> {
      [TypeField] = new IntDocumentValue(SchemaType),
      [SchemaVersionField] = new UIntDocumentValue(node.SchemaVersion),
      [ColumnsField] = new ArrayDocumentValue(node.Columns.Select(CollectionDescriptorDocument.WriteColumn).ToArray()),
      [ColumnsKnownField] = new BooleanDocumentValue(node.ColumnsKnown),
      [MigrationsField] = new ArrayDocumentValue(node.Migrations.Select(CollectionDescriptorDocument.WriteMigration).ToArray()),
      [UnknownField] = new BooleanDocumentValue(node.UnknownEarlierDeclarations)
    });
  }

  public static SchemaNode ReadSchemaNode(StoredRecord record) {
    var value = (ObjectDocumentValue)record.Document.Value;
    if (TypeOf(record.Document) != SchemaType) {
      throw new FormatException($"Document {record.Header.RecordId} is not a schema node.");
    }
    return new SchemaNode {
      Id = record.Header.RecordId,
      SchemaVersion = (ushort)ReadUInt(value, SchemaVersionField),
      Columns = ReadArray(value, ColumnsField).Select(CollectionDescriptorDocument.ReadColumn).ToList(),
      ColumnsKnown = ReadBoolean(value, ColumnsKnownField),
      Migrations = ReadArray(value, MigrationsField).Select(CollectionDescriptorDocument.ReadMigration).ToList(),
      UnknownEarlierDeclarations = ReadBoolean(value, UnknownField)
    };
  }

  public static ObjectDocument WriteRelationNode(RelationNode node) {
    return Document(node.Id, new Dictionary<string, IDocumentValue> {
      [TypeField] = new IntDocumentValue(RelationType),
      [KindField] = new StringDocumentValue(node.Kind.ToString()),
      [RelationField] = Relations.RelationDescriptorDocument.Write(node.Relation).Value,
      [UnknownField] = new BooleanDocumentValue(node.UnknownCreationTime)
    });
  }

  public static RelationNode ReadRelationNode(StoredRecord record) {
    var value = (ObjectDocumentValue)record.Document.Value;
    if (TypeOf(record.Document) != RelationType) {
      throw new FormatException($"Document {record.Header.RecordId} is not a relation node.");
    }
    var declaration = new ObjectDocument();
    declaration.SetIdentifierValue(new UlidDocumentValue(record.Header.RecordId));
    declaration.SetValue(value.Values.GetValueOrDefault(RelationField) ?? new ObjectDocumentValue());
    return new RelationNode {
      Id = record.Header.RecordId,
      Kind = Enum.Parse<RelationNodeKind>(ReadString(value, KindField)),
      Relation = Relations.RelationDescriptorDocument.Read(declaration),
      UnknownCreationTime = ReadBoolean(value, UnknownField)
    };
  }

  public static int TypeOf(ObjectDocument document) {
    return document.Value is ObjectDocumentValue value && value.Values.GetValueOrDefault(TypeField) is IntDocumentValue type
      ? type.Value
      : 0;
  }

  public static ObjectDocument WriteNode(VersionNode node) {
    var fields = new Dictionary<string, IDocumentValue> {
      [TypeField] = new IntDocumentValue(NodeType),
      //By name, so renumbering the enum cannot silently turn a delete into a restore.
      [KindField] = new StringDocumentValue(node.Kind.ToString()),
      [OperationField] = new UlidDocumentValue(node.OperationId),
      [SchemaVersionField] = new UIntDocumentValue(node.SchemaVersion),
      [DistanceField] = new IntDocumentValue(node.Distance)
    };
    if (node.Parent is { } parent) {
      fields[ParentField] = new UlidDocumentValue(parent);
    }
    if (node.Delta is not null) {
      fields[DeltaField] = node.Delta.ToDocumentValue();
    }
    if (node.Image is not null) {
      fields[ImageField] = node.Image;
      fields[ImageSchemaVersionField] = new UIntDocumentValue(node.ImageSchemaVersion);
    }
    if (node.ReplacedHead is { } replaced) {
      fields[ReplacedHeadField] = new UlidDocumentValue(replaced);
    }
    if (node.CutFrom is { } cutFrom) {
      fields[CutFromField] = new UlidDocumentValue(cutFrom);
    }
    return Document(node.VersionId, fields);
  }

  //The record is given by the caller: it is the index key's, not the document's (HS-5).
  public static VersionNode ReadNode(Ulid recordId, StoredRecord record) {
    var value = (ObjectDocumentValue)record.Document.Value;
    if (TypeOf(record.Document) != NodeType) {
      throw new FormatException($"Document {record.Header.RecordId} is not a version node.");
    }
    return new VersionNode {
      RecordId = recordId,
      VersionId = record.Header.RecordId,
      Kind = Enum.Parse<VersionKind>(ReadString(value, KindField)),
      Parent = ReadUlidOrNull(value, ParentField),
      OperationId = ReadUlid(value, OperationField),
      SchemaVersion = (ushort)ReadUInt(value, SchemaVersionField),
      Distance = ReadInt(value, DistanceField),
      Delta = value.Values.GetValueOrDefault(DeltaField) is { } delta ? DocumentDelta.FromDocumentValue(delta) : null,
      Image = value.Values.GetValueOrDefault(ImageField) as ObjectDocumentValue,
      ImageSchemaVersion = (ushort)ReadUInt(value, ImageSchemaVersionField),
      ReplacedHead = ReadUlidOrNull(value, ReplacedHeadField),
      CutFrom = ReadUlidOrNull(value, CutFromField)
    };
  }

  public static ObjectDocument WriteOperation(Operation operation) {
    return Document(operation.Id, new Dictionary<string, IDocumentValue> {
      [TypeField] = new IntDocumentValue(OperationType),
      [RecordedAtField] = new DateTimeDocumentValue(operation.RecordedAt),
      [AuthorField] = new StringDocumentValue(operation.Author ?? string.Empty),
      [CauseField] = new UlidDocumentValue(operation.Cause),
      [CommentField] = new StringDocumentValue(operation.Comment ?? string.Empty)
    });
  }

  public static Operation ReadOperation(StoredRecord record) {
    var value = (ObjectDocumentValue)record.Document.Value;
    if (TypeOf(record.Document) != OperationType) {
      throw new FormatException($"Document {record.Header.RecordId} is not an operation.");
    }
    return new Operation {
      Id = record.Header.RecordId,
      RecordedAt = value.Values.GetValueOrDefault(RecordedAtField) is DateTimeDocumentValue moment ? moment.Value : default,
      Author = ReadString(value, AuthorField),
      Cause = ReadUlid(value, CauseField),
      Comment = ReadString(value, CommentField)
    };
  }

  private static ObjectDocument Document(Ulid id, Dictionary<string, IDocumentValue> fields) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(id));
    document.SetValue(new ObjectDocumentValue(fields));
    return document;
  }

  private static string ReadString(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;
  }

  private static uint ReadUInt(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UIntDocumentValue number ? number.Value : default;
  }

  private static int ReadInt(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is IntDocumentValue number ? number.Value : default;
  }

  private static Ulid ReadUlid(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UlidDocumentValue identifier ? identifier.Value : default;
  }

  private static bool ReadBoolean(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is BooleanDocumentValue flag && flag.Value;
  }

  private static IDocumentValue[] ReadArray(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is ArrayDocumentValue array ? array.Values : [];
  }

  private static Ulid? ReadUlidOrNull(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UlidDocumentValue identifier ? identifier.Value : null;
  }
}
