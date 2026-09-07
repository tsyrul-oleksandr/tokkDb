using TokkDb.Buffer;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages;

//DC-7's lazy migration, on the read side: a record written under an older schema, read as
//though it had been written under the current one.
//
//Nothing on the page changes. The steps above the record's version are replayed over the
//values as they are handed out, so a collection whose column was renamed and retyped serves
//records written on either side of the change without either having been rewritten. Rewrite
//is what converges them, and after it there are no steps left to replay.
public sealed class SchemaMigrator {
  private readonly IReadOnlyList<ColumnMigration> _steps;

  private SchemaMigrator(IReadOnlyList<ColumnMigration> steps) {
    _steps = steps;
  }

  //Nothing to replay: the record was written under the current schema, or the collection has
  //never had a column renamed, retyped or removed. This is the case for almost every read,
  //so it costs one comparison and no allocation.
  public static readonly SchemaMigrator None = new([]);

  public bool IsIdentity => _steps.Count == 0;

  public static SchemaMigrator For(CollectionDescriptor descriptor, ushort recordVersion) {
    if (descriptor.Migrations.Count == 0 || recordVersion >= descriptor.SchemaVersion) {
      return None;
    }
    var steps = descriptor.Migrations
      .Where(migration => migration.Version > recordVersion)
      .OrderBy(migration => migration.Version)
      .ToArray();
    return steps.Length == 0 ? None : new SchemaMigrator(steps);
  }

  //The whole record, brought up to date. Used where the document itself is wanted — a
  //deserialization, or the rewrite that makes the migration permanent.
  public ObjectDocument Apply(ObjectDocument document) {
    if (IsIdentity || document.Value is not ObjectDocumentValue stored) {
      return document;
    }
    var fields = new Dictionary<string, IDocumentValue>(stored.Values, StringComparer.Ordinal);
    foreach (var step in _steps) {
      switch (step.Kind) {
        case ColumnMigrationKind.Rename:
          if (fields.Remove(step.ColumnName, out var renamed)) {
            fields[step.NewName] = renamed;
          }
          //A column added again under a name that was renamed away carries no old value, so
          //whatever the record held under the new name before this step is not that column's.
          else {
            fields.Remove(step.NewName);
          }
          break;
        case ColumnMigrationKind.Retype:
          if (fields.TryGetValue(step.ColumnName, out var retyped)) {
            fields[step.ColumnName] = ValueMigration.To(step.NewType, retyped);
          }
          break;
        default:
          fields.Remove(step.ColumnName);
          break;
      }
    }
    var migrated = new ObjectDocument();
    migrated.SetIdentifierValue(document.IdentifierValue);
    migrated.SetValue(new ObjectDocumentValue(fields));
    return migrated;
  }

  //One column, without touching the rest of the record. This is what the query path uses: a
  //predicate names a column or two, and reading the whole document to migrate it would undo
  //the buffer-level filtering of Phase 6.
  //
  //The name is traced backwards to what the record calls it, then the value is read as the
  //type the column now declares.
  public IDocumentValue Read(IFieldSource source, string columnName) {
    if (IsIdentity) {
      return source.GetField(columnName);
    }
    if (!TryTrace(columnName, out var storedName, out var retypedTo)) {
      //The trail is broken: the record predates a removal of a column this name was later
      //reused for, so it holds no value for the column being asked about.
      return null;
    }
    var value = source.GetField(storedName);
    return retypedTo is { } type ? ValueMigration.To(type, value) : value;
  }

  //What the record calls this column, and the type it now has to be read as. Walked newest
  //first, because the question is what the name used to be: a rename tells us the name before
  //it, and the newest retype is the type that ends up declared.
  private bool TryTrace(string columnName, out string storedName, out ValueTypeEnum? retypedTo) {
    storedName = columnName;
    retypedTo = null;
    for (var i = _steps.Count - 1; i >= 0; i--) {
      var step = _steps[i];
      switch (step.Kind) {
        case ColumnMigrationKind.Rename when step.NewName == storedName:
          storedName = step.ColumnName;
          break;
        case ColumnMigrationKind.Retype when step.ColumnName == storedName:
          retypedTo ??= step.NewType;
          break;
        case ColumnMigrationKind.Remove when step.ColumnName == storedName:
          return false;
        case ColumnMigrationKind.Rename when step.ColumnName == storedName:
          //The name was renamed away and something else now uses it. What the record holds
          //under it belongs to the column that left.
          return false;
      }
    }
    return true;
  }
}

//A record on the page, presented as the current schema describes it.
//
//It is an IFieldSource like the parsed object and the page buffer are, so a predicate reads
//through it without knowing that a migration is happening — which is what keeps the query
//path to one evaluator (DC-5) with lazy migration underneath it.
public sealed class MigratedFieldSource : IDocumentValue, IFieldSource {
  private readonly IFieldSource _stored;
  private readonly SchemaMigrator _migrator;

  public MigratedFieldSource(IFieldSource stored, SchemaMigrator migrator) {
    _stored = stored;
    _migrator = migrator;
  }

  public ValueTypeEnum Type => ValueTypeEnum.Object;

  public IDocumentValue GetField(string name) {
    return _migrator.Read(_stored, name);
  }

  public void WriteValue(BufferWriter writer) {
    throw new NotSupportedException(
      $"{nameof(MigratedFieldSource)} is a read-only view of a stored record and cannot be written.");
  }

  public void ReadValue(BufferReader reader) {
    throw new NotSupportedException(
      $"{nameof(MigratedFieldSource)} reads fields on demand and is not filled by a reader.");
  }
}
