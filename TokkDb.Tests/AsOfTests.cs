using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Tests.Fixtures;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//RH-3, RH-9, V-5 and V-8: the record at a moment, the four absences, and the schema at a
//moment.
public class AsOfTests(ITestOutputHelper output) {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, snapshotInterval: 8, largeDeltaRatio: 1.0);
    return db;
  }

  private static Person Aged(int age) {
    return new Person { Id = 1, Name = "Person-1", Age = age, Passport = new Passport("ST-1"), Tags = [] };
  }

  //The four absences are four outcomes, and a moment between two versions gives the earlier.
  [Fact]
  public void TheFourAbsencesAreFourOutcomes() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var beforeInsert = DateTimeOffset.UtcNow.AddSeconds(-5);
    var id = people.Insert(Aged(20));
    var inserted = people.History(id).Versions[0].LogicalTime;
    people.Update(id, Aged(21));
    people.Update(id, Aged(22));
    var second = people.History(id).Versions[1].LogicalTime;
    people.Delete(id);
    var deleted = people.History(id).Versions[^1].LogicalTime;

    Assert.Equal(AsOfOutcome.NotYetCreated, people.GetAsOf(id, beforeInsert).Outcome);
    Assert.Equal(AsOfOutcome.NotYetCreated, people.GetAsOf(id, inserted.AddMilliseconds(-1)).Outcome);
    var atInsert = people.GetAsOf(id, inserted);
    Assert.Equal(AsOfOutcome.Found, atInsert.Outcome);
    Assert.Equal(20, atInsert.Version.Value.Age);
    var atSecond = people.GetAsOf(id, second);
    Assert.Equal(AsOfOutcome.Found, atSecond.Outcome);
    Assert.Equal(21, atSecond.Version.Value.Age);
    Assert.Equal(AsOfOutcome.Deleted, people.GetAsOf(id, deleted).Outcome);
    Assert.Equal(AsOfOutcome.Deleted, people.GetAsOf(id, DateTimeOffset.UtcNow.AddHours(1)).Outcome);
    Assert.Equal(AsOfOutcome.NoSuchRecord, people.GetAsOf(Ulid.NewUlid(), DateTimeOffset.UtcNow).Outcome);

    //A record from before switch-on: before recorded history, until its Baseline, and its
    //first recorded change after.
    using var fixture = PreVersioningFixture.Copy();
    using var old = new TokkDbConnection(fixture.Path);
    old.Load();
    old.SetRetentionPolicy(PreVersioningFixture.Expenses, RetentionPolicy.KeepVersions);
    var expenses = old.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses);
    var expense = expenses.GetAllRecords().First(record => record.Value.Id == 7);
    Assert.Equal(AsOfOutcome.BeforeRecordedHistory, expenses.GetAsOf(expense.RecordId, DateTimeOffset.UtcNow).Outcome);
    var value = expense.Value;
    value.Amount = 1;
    expenses.Update(expense.RecordId, value);
    var history = expenses.History(expense.RecordId);
    Assert.Equal(AsOfOutcome.BeforeRecordedHistory, expenses.GetAsOf(expense.RecordId, history.Versions[0].LogicalTime.AddMilliseconds(-1)).Outcome);
    Assert.Equal(AsOfOutcome.Found, expenses.GetAsOf(expense.RecordId, history.Versions[0].LogicalTime).Outcome);
    Assert.Equal(1, expenses.GetAsOf(expense.RecordId, DateTimeOffset.UtcNow).Version.Value.Amount);
  }

  //RH-3's page bound: for a record with 1 000 versions, the lookup reads at most twice the
  //tree's height.
  [Fact]
  public void ALookupReadsAtMostTwiceTheTreesHeight() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(0));
    db.InTransaction(() => {
      for (var step = 1; step < 1_000; step++) {
        people.Update(id, Aged(step));
      }
    });
    var height = ((VersionStore)db.Versions).VersionIndex(Collection).Height();
    var versions = people.History(id).Versions;
    foreach (var index in new[] { 0, 1, 499, 998, 999 }) {
      var result = people.GetAsOf(id, versions[index].LogicalTime);
      Assert.Equal(AsOfOutcome.Found, result.Outcome);
      Assert.True(result.PagesRead <= 2 * height, $"{result.PagesRead} pages read at height {height}");
    }
    output.WriteLine($"tree height {height}; lookups read at most {2 * height} pages");
  }

  //RH-9: the schema as of each moment between a column added, a rename and a relation created.
  [Fact]
  public void SchemaAsOfReturnsWhatWasDeclaredAtEachMoment() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int), new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int), new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    db.CreateCollection("City", [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true)]);
    var beforeSwitchOn = DateTimeOffset.UtcNow.AddSeconds(-5);
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    var switchedOn = db.Versions.SchemaNodes(Collection)[0].LogicalTime;
    var columns = db.Collection(Collection).Columns.ToList();
    columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
    db.SetColumns(Collection, columns);
    var added = db.Versions.SchemaNodes(Collection)[1].LogicalTime;
    db.SetColumns(Collection, columns.Select(column => column.Name == "Age" ? new ColumnDescriptor("Years", ValueTypeEnum.Int) : column),
      [ColumnMigration.Rename(0, "Age", "Years")]);
    var renamed = db.Versions.SchemaNodes(Collection)[2].LogicalTime;
    db.CreateRelation("PersonCity", Collection, "City", "City", "Name");
    var related = db.Versions.RelationNodes(Collection)[0].LogicalTime;

    Assert.True(db.SchemaAsOf(Collection, beforeSwitchOn).BeforeRecordedHistory);
    var atSwitchOn = db.SchemaAsOf(Collection, switchedOn);
    Assert.False(atSwitchOn.BeforeRecordedHistory);
    Assert.Equal(1, atSwitchOn.Schema.SchemaVersion);
    Assert.DoesNotContain(atSwitchOn.Schema.Columns, column => column.Name == "City");
    Assert.Empty(atSwitchOn.Relations);
    var atAdded = db.SchemaAsOf(Collection, added);
    Assert.Equal(2, atAdded.Schema.SchemaVersion);
    Assert.Contains(atAdded.Schema.Columns, column => column.Name == "City");
    Assert.Contains(atAdded.Schema.Columns, column => column.Name == "Age");
    var atRenamed = db.SchemaAsOf(Collection, renamed);
    Assert.Equal(3, atRenamed.Schema.SchemaVersion);
    Assert.Contains(atRenamed.Schema.Columns, column => column.Name == "Years");
    Assert.Empty(atRenamed.Relations);
    var atRelated = db.SchemaAsOf(Collection, related);
    Assert.Equal("PersonCity", Assert.Single(atRelated.Relations).Name);
    db.RemoveRelation("PersonCity");
    Assert.Empty(db.SchemaAsOf(Collection, DateTimeOffset.UtcNow.AddMinutes(1)).Relations);
    Assert.Single(db.SchemaAsOf(Collection, related).Relations);
  }
}

//N-10 and HS-7 across a stepped-back clock and a restart. Runs alone: it overrides the
//engine's clock and resets its identifier source.
[Collection(EngineClockCollection.Name)]
public class AsOfAcrossClockStepsTests {
  private const string Collection = nameof(Person);

  private static Person Aged(int age) {
    return new Person { Id = 1, Name = "Person-1", Age = age, Passport = new Passport("ST-1"), Tags = [] };
  }

  [Fact]
  public void AClockThatStepsBackDuringWritesLeavesAnswersMonotoneAndHistoryFlagsTheStep() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, largeDeltaRatio: 1.0);
    var people = db.Entities<Person>();
    var start = DateTimeOffset.UtcNow;
    //The clock as each write sees it: forward, then an hour back, then forward again.
    var now = start;
    Ulid id = default;
    using (EngineClock.Override(() => now)) {
      id = people.Insert(Aged(0));
      now = start.AddSeconds(1);
      people.Update(id, Aged(1));
      now = start.AddHours(-1);
      people.Update(id, Aged(2));
      now = start.AddHours(-1).AddSeconds(1);
      people.Update(id, Aged(3));
      now = start.AddSeconds(2);
      people.Update(id, Aged(4));
    }

    var history = people.History(id);
    var versions = history.Versions;
    for (var i = 1; i < versions.Count; i++) {
      Assert.True(versions[i].VersionId.CompareTo(versions[i - 1].VersionId) > 0);
      Assert.True(versions[i].LogicalTime >= versions[i - 1].LogicalTime, "logical time ran backwards");
      Assert.True(versions[i].LogicalTime >= versions[i].RecordedAt.AddMilliseconds(-1), "logical time is before recorded time");
    }
    //The operation recorded an hour earlier than the one before it is flagged.
    Assert.NotEmpty(history.OperationsRecordedOutOfOrder);
    Assert.Contains(versions[2].OperationId, history.OperationsRecordedOutOfOrder);

    //Answers are monotone in the moment.
    var previousAge = -1;
    foreach (var version in versions) {
      var age = people.GetAsOf(id, version.LogicalTime).Version.Value.Age;
      Assert.True(age >= previousAge);
      previousAge = age;
    }
    Assert.Equal(4, people.GetAsOf(id, DateTimeOffset.UtcNow.AddHours(2)).Version.Value.Age);
  }

  [Fact]
  public void AfterAReopenWithTheClockBehindNewVersionsAndSchemaNodesSortAfterWhatWasStored() {
    using var file = new TempDatabaseFile();
    Ulid id;
    Ulid head;
    DateTimeOffset headTime;
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, largeDeltaRatio: 1.0);
      var people = db.Entities<Person>();
      id = people.Insert(Aged(0));
      people.Update(id, Aged(1));
      head = people.HeadVersion(id);
      headTime = head.Time;
    }

    RecordIdentity.ResetForTests();
    using (EngineClock.Override(() => DateTimeOffset.UtcNow.AddHours(-1))) {
      using var reopened = new TokkDbConnection(file.Path);
      reopened.Load();
      var people = reopened.Entities<Person>();
      people.Update(id, Aged(2));
      var next = people.HeadVersion(id);
      Assert.True(next.CompareTo(head) > 0);
      Assert.True(next.Time >= headTime);
      //Found at its own logical time, and the previous head at any earlier moment — with the
      //clock behind, its logical time may share the head's millisecond, and then there is no
      //earlier moment inside it.
      var atNext = people.GetAsOf(id, next.Time);
      Assert.Equal(AsOfOutcome.Found, atNext.Outcome);
      Assert.Equal(2, atNext.Version.Value.Age);
      if (next.Time > headTime) {
        Assert.Equal(1, people.GetAsOf(id, headTime).Version.Value.Age);
        Assert.Equal(1, people.GetAsOf(id, next.Time.AddMilliseconds(-1)).Version.Value.Age);
      }
      var ages = new[] { headTime.AddSeconds(-1), headTime, next.Time, next.Time.AddSeconds(1) }
        .Select(moment => people.GetAsOf(id, moment)).Where(result => result.IsFound).Select(result => result.Version.Value.Age).ToList();
      Assert.Equal(ages.Order(), ages);

      //A schema node written now sorts after the one before it, so SchemaAsOf stays monotone.
      var before = reopened.Versions.SchemaNodes(Collection)[^1];
      var columns = reopened.Collection(Collection).Columns.ToList();
      columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
      reopened.SetColumns(Collection, columns);
      var after = reopened.Versions.SchemaNodes(Collection)[^1];
      Assert.True(after.Id.CompareTo(before.Id) > 0);
      Assert.True(after.LogicalTime >= before.LogicalTime);
      Assert.Equal(2, reopened.SchemaAsOf(Collection, after.LogicalTime).Schema.SchemaVersion);
      Assert.True(reopened.SchemaAsOf(Collection, before.LogicalTime.AddMilliseconds(-1)).BeforeRecordedHistory);
      //Monotone in the moment, whatever millisecond the new node landed in.
      var versions = new[] { before.LogicalTime, after.LogicalTime, after.LogicalTime.AddSeconds(1) }
        .Select(moment => reopened.SchemaAsOf(Collection, moment).Schema.SchemaVersion).ToList();
      Assert.Equal(versions.Order(), versions);
    }
  }
}
