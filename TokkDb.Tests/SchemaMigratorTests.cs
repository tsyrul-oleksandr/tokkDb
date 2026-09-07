using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//DC-7's lazy migration on its own: the replay of a schema change over a record written before
//it, with no database in sight. What the steps mean is settled here; that they are recorded
//and replayed in the right places is settled end to end.
public class SchemaMigratorTests {
  private static CollectionDescriptor Collection(ushort version, params ColumnMigration[] migrations) {
    return new CollectionDescriptor {
      Name = "Person", SchemaVersion = version, Migrations = migrations.ToList()
    };
  }

  private static ObjectDocument Record(params (string Field, IDocumentValue Value)[] fields) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(Ulid.NewUlid()));
    document.SetValue(new ObjectDocumentValue(
      fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal)));
    return document;
  }

  private static ObjectDocumentValue Fields(ObjectDocument document) {
    return (ObjectDocumentValue)document.Value;
  }

  //The common case, and the one that has to cost nothing: a record written under the current
  //schema, or a collection that has never had a column changed.
  [Fact]
  public void ARecordAtTheCurrentVersionIsNotMigratedAtAll() {
    var descriptor = Collection(3, ColumnMigration.Rename(2, "Name", "FullName"));

    Assert.True(SchemaMigrator.For(descriptor, 3).IsIdentity);
    Assert.True(SchemaMigrator.For(descriptor, 4).IsIdentity);
    Assert.True(SchemaMigrator.For(Collection(3), 1).IsIdentity);
    Assert.False(SchemaMigrator.For(descriptor, 1).IsIdentity);
  }

  //Only the steps above the record's version. A record written after a rename must not have
  //it applied a second time.
  [Fact]
  public void OnlyTheStepsAboveTheRecordsOwnVersionAreReplayed() {
    var descriptor = Collection(3,
      ColumnMigration.Rename(2, "Name", "FullName"),
      ColumnMigration.Remove(3, "Age"));

    var old = SchemaMigrator.For(descriptor, 1).Apply(
      Record(("Name", new StringDocumentValue("Olena")), ("Age", new IntDocumentValue(31))));
    var newer = SchemaMigrator.For(descriptor, 2).Apply(
      Record(("FullName", new StringDocumentValue("Ivan")), ("Age", new IntDocumentValue(29))));

    Assert.Equal("Olena", ((StringDocumentValue)Fields(old)["FullName"]).Value);
    Assert.False(Fields(old).Values.ContainsKey("Name"));
    Assert.False(Fields(old).Values.ContainsKey("Age"));
    //Written after the rename, so only the removal applies.
    Assert.Equal("Ivan", ((StringDocumentValue)Fields(newer)["FullName"]).Value);
    Assert.False(Fields(newer).Values.ContainsKey("Age"));
  }

  [Fact]
  public void ARetypeReadsTheStoredValueAsTheTypeTheColumnNowDeclares() {
    var descriptor = Collection(2, ColumnMigration.Retype(2, "Age", ValueTypeEnum.Long));

    var migrated = SchemaMigrator.For(descriptor, 1).Apply(Record(("Age", new IntDocumentValue(31))));

    //Long has no document value of its own, so it is stored as invariant text — the same form
    //a record written after the retype is written in, which is what makes the two comparable.
    Assert.Equal("31", ((StringDocumentValue)Fields(migrated)["Age"]).Value);
  }

  //Steps stack, and the order they are replayed in is the order they happened in.
  [Fact]
  public void ARenameFollowedByARetypeIsReplayedInOrder() {
    var descriptor = Collection(3,
      ColumnMigration.Rename(2, "Age", "Years"),
      ColumnMigration.Retype(3, "Years", ValueTypeEnum.Decimal));

    var migrated = SchemaMigrator.For(descriptor, 1).Apply(Record(("Age", new IntDocumentValue(31))));

    Assert.Equal("31", ((StringDocumentValue)Fields(migrated)["Years"]).Value);
    Assert.False(Fields(migrated).Values.ContainsKey("Age"));
  }

  //A retype the old value cannot survive leaves the record without a value for that column —
  //the same situation as a record written before the column existed, which a read already
  //knows what to do with.
  [Fact]
  public void AValueThatCannotBeReadAsTheNewTypeBecomesNothing() {
    var descriptor = Collection(2, ColumnMigration.Retype(2, "Age", ValueTypeEnum.Int));

    var migrated = SchemaMigrator.For(descriptor, 1)
      .Apply(Record(("Age", new StringDocumentValue("not a number"))));

    Assert.IsType<NullDocumentValue>(Fields(migrated)["Age"]);
  }

  //The field-wise read the query path uses has to agree with the whole-document one, or a
  //predicate and the record it matched would disagree about what the record says.
  [Theory]
  [InlineData("FullName")]
  [InlineData("Years")]
  [InlineData("Gone")]
  [InlineData("NeverExisted")]
  public void ReadingOneFieldAgreesWithMigratingTheWholeDocument(string column) {
    var descriptor = Collection(4,
      ColumnMigration.Rename(2, "Name", "FullName"),
      ColumnMigration.Rename(3, "Age", "Years"),
      ColumnMigration.Retype(3, "Years", ValueTypeEnum.Long),
      ColumnMigration.Remove(4, "Gone"));
    var migrator = SchemaMigrator.For(descriptor, 1);
    var stored = Record(
      ("Name", new StringDocumentValue("Olena")),
      ("Age", new IntDocumentValue(31)),
      ("Gone", new StringDocumentValue("obsolete")));

    var wholeDocument = Fields(migrator.Apply(stored)).Values.GetValueOrDefault(column);
    var oneField = migrator.Read((IFieldSource)stored.Value, column);

    Assert.Equal(wholeDocument?.Type, oneField?.Type);
    Assert.Equal(Describe(wholeDocument), Describe(oneField));
  }

  //A name that was removed and later given to a new column: a record written before the
  //removal holds the old column's value under it, and that value is not this column's.
  [Fact]
  public void AColumnNameReusedAfterARemovalDoesNotResurrectTheOldValue() {
    var descriptor = Collection(3, ColumnMigration.Remove(2, "Code"));
    var migrator = SchemaMigrator.For(descriptor, 1);
    var stored = Record(("Code", new StringDocumentValue("old-meaning")));

    Assert.Null(migrator.Read((IFieldSource)stored.Value, "Code"));
    Assert.False(Fields(migrator.Apply(stored)).Values.ContainsKey("Code"));
  }

  //The same trap on a rename: the name was given away, so what the record holds under it
  //belongs to the column that left rather than to whatever now answers to it.
  [Fact]
  public void ANameRenamedAwayDoesNotAnswerForAColumnLaterGivenIt() {
    var descriptor = Collection(2, ColumnMigration.Rename(2, "Name", "FullName"));
    var migrator = SchemaMigrator.For(descriptor, 1);
    var stored = Record(("Name", new StringDocumentValue("Olena")));

    Assert.Equal("Olena",
      ((StringDocumentValue)migrator.Read((IFieldSource)stored.Value, "FullName")).Value);
    Assert.Null(migrator.Read((IFieldSource)stored.Value, "Name"));
  }

  private static string Describe(IDocumentValue value) {
    return value switch {
      null => "<absent>",
      NullDocumentValue => "<null>",
      StringDocumentValue text => text.Value,
      IntDocumentValue number => number.Value.ToString(),
      _ => value.Type.ToString()
    };
  }
}
