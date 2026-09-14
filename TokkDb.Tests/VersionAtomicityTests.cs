using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//WV-7 and WV-10: a node, its index entry, the operation, any keyframe copy, any schema node,
//the retirement and the new image commit together or not at all, and the invariants hold on
//either side of every write the fault injector can stop.
public class VersionAtomicityTests(ITestOutputHelper output) {
  private const string Collection = nameof(Person);

  private static List<ColumnDescriptor> PersonColumns() {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  //A versioned collection with three records, at k = 1 so that every update copies an image.
  private static List<Ulid> Baseline(TempDatabaseFile file) {
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, snapshotInterval: 1);
    var people = db.Entities<Person>();
    return Enumerable.Range(0, 3).Select(i => people.Insert(TestPeople.Numbered(i))).ToList();
  }

  private record Outcome(bool Fired, string FiredAt);

  //Runs the operation once cleanly to learn how many writes it makes, then once per write with
  //the fault landing there, checking the state after a reopen every time.
  private void KillAtEveryWrite(string name, Action<TokkDbConnection, List<Ulid>> operation,
      Func<TokkDbConnection, List<Ulid>, bool> landed, Action<TokkDbConnection, List<Ulid>, bool> assertBeforeOrAfter) {
    int writes;
    using (var file = new TempDatabaseFile()) {
      var ids = Baseline(file);
      using var disk = new FaultInjectingDiskManager(file.Path);
      using (var db = new TokkDbConnection(disk)) {
        db.Load();
        operation(db, ids);
      }
      writes = disk.WriteCount;
    }
    Assert.True(writes > 3, $"{name}: only {writes} writes to aim at");

    //One past the last write as well: the fault then never fires, and that is the run in which
    //the operation lands whole — every kill up to and including the commit record takes it back.
    var before = 0;
    var after = 0;
    for (var killAfter = 1; killAfter <= writes + 1; killAfter++) {
      using var file = new TempDatabaseFile();
      var ids = Baseline(file);
      var disk = new FaultInjectingDiskManager(file.Path, killAfter);
      var attempt = new TokkDbConnection(disk);
      var fired = false;
      try {
        attempt.Load();
        operation(attempt, ids);
      } catch (SimulatedProcessKillException) {
        fired = true;
      } finally {
        attempt.Dispose();
      }
      Assert.Equal(killAfter <= writes, fired);

      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var verification = reopened.Versions.Verify(Collection);
      Assert.True(verification.IsSound, $"{name}, killed at write {killAfter} ({disk.FiredAt}): {verification}");
      var committed = landed(reopened, ids);
      assertBeforeOrAfter(reopened, ids, committed);
      if (committed) {
        after++;
      } else {
        before++;
      }
    }
    output.WriteLine($"{name}: {writes} writes; the state before was found {before} times, the state after {after} times");
    Assert.True(after > 0, $"{name}: the operation never landed, so the after state was never checked");
    Assert.True(before > 0, $"{name}: the operation always landed, so the before state was never checked");
  }

  private static void Insert(TokkDbConnection db, List<Ulid> ids) {
    db.Entities<Person>().Insert(TestPeople.Numbered(10));
  }

  private static void UpdateWithKeyframeCopy(TokkDbConnection db, List<Ulid> ids) {
    var people = db.Entities<Person>();
    var value = people.GetById(ids[0]).Value;
    value.Age = 99;
    people.Update(ids[0], value);
  }

  private static void Delete(TokkDbConnection db, List<Ulid> ids) {
    db.Entities<Person>().Delete(ids[1]);
  }

  private static void SchemaChange(TokkDbConnection db, List<Ulid> ids) {
    var columns = PersonColumns();
    columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
    db.SetColumns(Collection, columns);
  }

  [Fact]
  public void AnInsertIsAllOrNothing() {
    KillAtEveryWrite("insert", Insert, (db, _) => db.Entities<Person>().GetAll().Count() == 4, (db, ids, landed) => {
      var people = db.Entities<Person>();
      if (landed) {
        var added = Assert.Single(people.GetAllRecords(), record => record.Value.Id == 10);
        var node = Assert.Single(db.Versions.Nodes(Collection, added.RecordId));
        Assert.Equal(VersionKind.Insert, node.Kind);
        Assert.NotNull(db.Versions.Operation(Collection, node.OperationId));
      } else {
        Assert.Equal(3, people.GetAll().Count());
      }
    });
  }

  [Fact]
  public void AnUpdateWithAKeyframeCopyIsAllOrNothing() {
    KillAtEveryWrite("update", UpdateWithKeyframeCopy, (db, ids) => db.Entities<Person>().GetById(ids[0]).Value.Age == 99, (db, ids, landed) => {
      var people = db.Entities<Person>();
      var nodes = db.Versions.Nodes(Collection, ids[0]);
      if (landed) {
        Assert.Equal(99, people.GetById(ids[0]).Value.Age);
        Assert.Equal(2, nodes.Count);
        Assert.NotNull(nodes[0].Image);
        Assert.Equal(nodes[1].VersionId, people.HeadVersion(ids[0]));
      } else {
        Assert.Equal(20, people.GetById(ids[0]).Value.Age);
        var only = Assert.Single(nodes);
        Assert.Null(only.Image);
      }
    });
  }

  [Fact]
  public void ADeleteIsAllOrNothing() {
    KillAtEveryWrite("delete", Delete, (db, ids) => db.Entities<Person>().GetById(ids[1]) is null, (db, ids, landed) => {
      var people = db.Entities<Person>();
      var nodes = db.Versions.Nodes(Collection, ids[1]);
      if (landed) {
        Assert.Null(people.GetById(ids[1]));
        Assert.Equal(2, nodes.Count);
        Assert.Equal(VersionKind.Delete, nodes[1].Kind);
        Assert.NotNull(nodes[0].Image);
      } else {
        Assert.NotNull(people.GetById(ids[1]));
        Assert.Single(nodes);
      }
    });
  }

  [Fact]
  public void ASchemaChangeIsAllOrNothing() {
    KillAtEveryWrite("schema change", SchemaChange, (db, _) => db.Collection(Collection).SchemaVersion == 2, (db, ids, landed) => {
      var schemas = db.Versions.SchemaNodes(Collection);
      if (landed) {
        Assert.Equal(2, db.Collection(Collection).SchemaVersion);
        Assert.Equal(2, schemas.Count);
        Assert.Contains(schemas[1].Columns, column => column.Name == "City");
      } else {
        Assert.Equal(1, db.Collection(Collection).SchemaVersion);
        Assert.Single(schemas);
      }
      Assert.Equal(3, db.Entities<Person>().GetAll().Count());
    });
  }
}
