using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Tests.Fixtures;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//HS-10, V-11 and WV-9: schema and relation history live in the history collection, are
//recorded in the transaction of the change, and are read from memory.
public class SchemaHistoryTests {
  private const string Collection = nameof(Person);

  private static List<ColumnDescriptor> PersonColumns() {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  private static TokkDbConnection NewVersioned(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    return db;
  }

  [Fact]
  public void SwitchOnRecordsTheCurrentSchemaAndEachChangeAddsExactlyOneNode() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var store = db.Versions;

    var initial = Assert.Single(store.SchemaNodes(Collection));
    Assert.Equal(1, initial.SchemaVersion);
    Assert.True(initial.ColumnsKnown);
    Assert.False(initial.UnknownEarlierDeclarations);
    Assert.Equal(PersonColumns().Select(column => column.Name), initial.Columns.Select(column => column.Name));
    Assert.True(initial.Columns.Single(column => column.Name == "Name").Unique);
    Assert.Empty(store.RelationNodes(Collection));

    //A column added: one node, no migration step.
    var columns = PersonColumns();
    columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
    db.SetColumns(Collection, columns);
    Assert.Equal(2, store.SchemaNodes(Collection).Count);
    var added = store.SchemaNodes(Collection)[1];
    Assert.Equal(2, added.SchemaVersion);
    Assert.Empty(added.Migrations);
    Assert.Contains(added.Columns, column => column.Name == "City");
    Assert.True(added.Id.CompareTo(initial.Id) > 0);

    //A rename and a retype in one change: one node with both steps.
    var renamed = columns.Select(column => column.Name == "Age"
      ? new ColumnDescriptor("Years", ValueTypeEnum.Long)
      : column).ToList();
    db.SetColumns(Collection, renamed, [
      ColumnMigration.Rename(0, "Age", "Years"),
      ColumnMigration.Retype(0, "Years", ValueTypeEnum.Long)
    ]);
    Assert.Equal(3, store.SchemaNodes(Collection).Count);
    var changed = store.SchemaNodes(Collection)[2];
    Assert.Equal(3, changed.SchemaVersion);
    Assert.Equal(2, changed.Migrations.Count);
    Assert.All(changed.Migrations, step => Assert.Equal(3, step.Version));

    //Relations: one node per creation and per removal, in the history of every versioned
    //collection the relation names.
    db.CreateRelation("PersonCity", Collection, "City", "City", "Name");
    var created = Assert.Single(store.RelationNodes(Collection));
    Assert.Equal(RelationNodeKind.Created, created.Kind);
    Assert.False(created.UnknownCreationTime);
    Assert.Equal("PersonCity", created.Relation.Name);
    Assert.Equal("City", created.Relation.TargetCollection);
    Assert.Throws<InvalidOperationException>(() => store.RelationNodes("City"));

    db.RemoveRelation("PersonCity");
    Assert.Equal(2, store.RelationNodes(Collection).Count);
    Assert.Equal(RelationNodeKind.Removed, store.RelationNodes(Collection)[1].Kind);
    Assert.Equal(3, store.SchemaNodes(Collection).Count);

    //WV-9: none of it created a record version, and the history holds exactly the nodes counted.
    var types = db.SystemDocuments.ReadAll(HistoryCollections.NameFor(db.Collection(Collection).Id))
      .Select(entry => HistoryDocuments.TypeOf(entry.Document)).ToList();
    Assert.DoesNotContain(HistoryDocuments.NodeType, types);
    Assert.DoesNotContain(HistoryDocuments.OperationType, types);
    Assert.Equal(3, types.Count(type => type == HistoryDocuments.SchemaType));
    Assert.Equal(2, types.Count(type => type == HistoryDocuments.RelationType));
  }

  [Fact]
  public void ARolledBackChangeAddsNoNode() {
    using var file = new TempDatabaseFile();
    int before;
    using (var db = NewVersioned(file)) {
      var store = db.Versions;
      var people = db.Entities<Person>(Collection);
      people.Insert(TestPeople.Numbered(1));
      people.Insert(TestPeople.Numbered(41));
      before = store.SchemaNodes(Collection).Count;

      //Declaring Age unique over two records that share one refuses after the schema node has
      //been recorded, when the unique index is built: the descriptor and the node roll back together.
      Assert.Throws<Pages.Indexes.UniqueConstraintViolationException>(() => db.SetColumns(Collection,
        PersonColumns().Select(column => column.Name == "Age"
          ? new ColumnDescriptor("Age", ValueTypeEnum.Int, unique: true) : column)));

      Assert.Equal(before, store.SchemaNodes(Collection).Count);
      Assert.Equal(1, db.Collection(Collection).SchemaVersion);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(before, reopened.Versions.SchemaNodes(Collection).Count);
  }

  //HS-10 on the step 0.2 fixture: the pending rename and the relation are recorded at switch-on,
  //marked as reaching back to before recorded history.
  [Fact]
  public void OnTheFixtureSwitchOnRecordsThePendingRenameAndTheRelation() {
    using var file = PreVersioningFixture.Copy();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.SetRetentionPolicy(PreVersioningFixture.Expenses, RetentionPolicy.KeepVersions);
    var store = db.Versions;

    var schemas = store.SchemaNodes(PreVersioningFixture.Expenses);
    var current = Assert.Single(schemas);
    Assert.Equal(PreVersioningFixture.ExpenseSchemaVersion, current.SchemaVersion);
    Assert.True(current.ColumnsKnown);
    Assert.True(current.UnknownEarlierDeclarations);
    var rename = Assert.Single(current.Migrations);
    Assert.Equal(ColumnMigrationKind.Rename, rename.Kind);
    Assert.Equal("Note", rename.ColumnName);
    Assert.Equal("Comment", rename.NewName);
    Assert.Contains(current.Columns, column => column.Name == "Comment");

    var relation = Assert.Single(store.RelationNodes(PreVersioningFixture.Expenses));
    Assert.Equal(RelationNodeKind.Created, relation.Kind);
    Assert.True(relation.UnknownCreationTime);
    Assert.Equal(PreVersioningFixture.Relation, relation.Relation.Name);

    //The steps a version-1 record is read through, from history rather than the catalogue.
    var steps = store.SchemaAt(PreVersioningFixture.Expenses, 1, 2);
    Assert.Equal(ColumnMigrationKind.Rename, Assert.Single(steps).Kind);
    Assert.Empty(store.SchemaAt(PreVersioningFixture.Expenses, 2, 2));
  }

  [Fact]
  public void SchemaAtAfterAReopenReadsNoPageAndSurvivesRewrite() {
    using var file = PreVersioningFixture.Copy();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      db.SetRetentionPolicy(PreVersioningFixture.Expenses, RetentionPolicy.KeepVersions);
    }

    using (var reopened = new TokkDbConnection(file.Path)) {
      reopened.Load();
      var readsBefore = reopened.PageReadCount;
      var steps = reopened.Versions.SchemaAt(PreVersioningFixture.Expenses, 1, 2);
      var nodes = reopened.Versions.SchemaNodes(PreVersioningFixture.Expenses);
      var relations = reopened.Versions.RelationNodes(PreVersioningFixture.Expenses);
      Assert.Equal(readsBefore, reopened.PageReadCount);
      Assert.Single(steps);
      Assert.Single(nodes);
      Assert.Single(relations);

      //Rewrite converges the records and empties the catalogue's log; history keeps the rename.
      Assert.Equal(PreVersioningFixture.ExpensesBeforeRename - 1, reopened.Rewrite(PreVersioningFixture.Expenses));
      Assert.Empty(reopened.Collection(PreVersioningFixture.Expenses).Migrations);
      Assert.Single(reopened.Versions.SchemaAt(PreVersioningFixture.Expenses, 1, 2));
      Assert.Single(reopened.Versions.SchemaNodes(PreVersioningFixture.Expenses));
    }

    using var again = new TokkDbConnection(file.Path);
    again.Load();
    Assert.Single(again.Versions.SchemaAt(PreVersioningFixture.Expenses, 1, 2));
  }

  //A collection that keeps no versions records nothing, and switching on later records what
  //stands then.
  [Fact]
  public void AnUnversionedCollectionRecordsNothing() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    db.InTransaction(() => Assert.Null(db.Versions.RecordSchema(Collection, SchemaNode.Of(db.Collection(Collection), []))));
    var columns = PersonColumns();
    columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
    db.SetColumns(Collection, columns);
    Assert.Throws<InvalidOperationException>(() => db.Versions.SchemaNodes(Collection));

    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    var node = Assert.Single(db.Versions.SchemaNodes(Collection));
    Assert.Equal(2, node.SchemaVersion);
    Assert.True(node.UnknownEarlierDeclarations);
    Assert.Contains(node.Columns, column => column.Name == "City");
  }
}
