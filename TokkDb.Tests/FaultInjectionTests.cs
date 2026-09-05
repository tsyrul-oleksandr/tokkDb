using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//NFR-3: no committed transaction is lost and no partially applied transaction survives a
//process kill. The kill is simulated by stopping every write at a chosen point, which with
//unbuffered writes leaves the file exactly as a killed process would.
public class FaultInjectionTests {
  private const int Runs = 100;
  private const int BaselineRecords = 8;
  private const int WorkloadRecords = 12;

  private readonly ITestOutputHelper _output;

  public FaultInjectionTests(ITestOutputHelper output) {
    _output = output;
  }

  [Fact]
  public void AHundredRandomisedKillsDuringWritesAllLeaveAnOpenableConsistentDatabase() {
    var writesInACleanRun = MeasureWritesInACleanRun();
    Assert.True(writesInACleanRun > WorkloadRecords, $"only {writesInACleanRun} writes to aim at");

    var outcomes = new Dictionary<RecoveryOutcome, int>();
    var fired = 0;
    for (var seed = 1; seed <= Runs; seed++) {
      //The seed alone decides where the kill lands, so any failing run can be repeated.
      var killAfterWrites = new Random(seed).Next(1, writesInACleanRun + 1);
      var result = RunOnce(seed, killAfterWrites);
      if (result.Fired) {
        fired++;
      }
      outcomes[result.Outcome] = outcomes.GetValueOrDefault(result.Outcome) + 1;
    }

    foreach (var (outcome, count) in outcomes.OrderBy(entry => entry.Key)) {
      _output.WriteLine($"{outcome}: {count}");
    }
    //A run where the fault never fired would prove nothing, so most of them must have.
    Assert.True(fired > Runs / 2, $"only {fired} of {Runs} runs were actually interrupted");
    Assert.Contains(RecoveryOutcome.UncommittedTransactionRolledBack, outcomes.Keys);
  }

  [Fact]
  public void TheSameSeedKillsAtTheSamePlaceEveryTime() {
    var writesInACleanRun = MeasureWritesInACleanRun();
    var killAfterWrites = new Random(4242).Next(1, writesInACleanRun + 1);

    var first = RunOnce(4242, killAfterWrites);
    var second = RunOnce(4242, killAfterWrites);

    Assert.Equal(first.FiredAt, second.FiredAt);
    Assert.Equal(first.Outcome, second.Outcome);
    Assert.Equal(first.RecordCount, second.RecordCount);
  }

  private record RunResult(bool Fired, string FiredAt, RecoveryOutcome Outcome, int RecordCount);

  private static int MeasureWritesInACleanRun() {
    using var file = new TempDatabaseFile();
    CreateBaseline(file);
    using var disk = new FaultInjectingDiskManager(file.Path);
    using (var db = new TokkDbConnection(disk)) {
      db.Load();
      RunWorkload(db);
    }
    return disk.WriteCount;
  }

  private RunResult RunOnce(int seed, int killAfterWrites) {
    using var file = new TempDatabaseFile();
    CreateBaseline(file);

    var fired = false;
    string firedAt = null;
    var disk = new FaultInjectingDiskManager(file.Path, killAfterWrites);
    var db = new TokkDbConnection(disk);
    try {
      db.Load();
      RunWorkload(db);
    } catch (SimulatedProcessKillException) {
      fired = true;
      firedAt = disk.FiredAt;
    } finally {
      //Closing the handles is all a killed process does; the injector refuses any write, so
      //nothing here can tidy up after the interrupted transaction.
      db.Dispose();
    }

    return Verify(file, seed, killAfterWrites, fired, firedAt);
  }

  private RunResult Verify(TempDatabaseFile file, int seed, int killAfterWrites, bool fired, string firedAt) {
    var where = $"seed {seed}, kill after write {killAfterWrites}" +
      (fired ? $" ({firedAt})" : " (never fired)");

    using var reopened = new TokkDbConnection(file.Path);
    var decision = reopened.RecoveryDecision;
    reopened.Load();

    //Every collection reads back, every page of it checksums, and what the catalogue says it
    //holds is what it holds. A transaction applied to the records but not to the catalogue —
    //or the other way round — shows up right here.
    foreach (var collection in reopened.Collections) {
      var rows = CountRows(reopened, collection);
      Assert.True(collection.RecordCount == rows,
        $"{where}: {collection.Name} says {collection.RecordCount} records but holds {rows}");
    }

    var people = reopened.Entities<Person>().GetAll().ToList();
    //Nothing committed before the kill may be missing.
    Assert.True(people.Count >= BaselineRecords,
      $"{where}: {people.Count} records survived, fewer than the {BaselineRecords} committed before the run");
    Assert.True(people.Count <= BaselineRecords + WorkloadRecords,
      $"{where}: {people.Count} records survived, more than were ever inserted");
    //And every surviving record is whole.
    Assert.All(people, person => Assert.Equal($"Person-{person.Id}", person.Name));
    Assert.Equal(people.Count, people.Select(person => person.Id).Distinct().Count());

    return new RunResult(fired, firedAt, decision.Outcome, people.Count);
  }

  private static int CountRows(TokkDbConnection db, CollectionDescriptor collection) {
    if (collection.Name == SystemCollections.Collections) {
      //The catalogue counts itself among its own records.
      return db.Collections.Count;
    }
    if (collection.IsSystem) {
      return 0;
    }
    return collection.Name == "Person"
      ? db.Entities<Person>().GetAll().Count()
      : db.Entities<Tag>(collection.Name).GetAll().Count();
  }

  private static void CreateBaseline(TempDatabaseFile file) {
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();
    for (var i = 0; i < BaselineRecords; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }
  }

  //A mixture of record inserts and a catalogue change, so the kill can land in either.
  private static void RunWorkload(TokkDbConnection db) {
    var entities = db.Entities<Person>();
    for (var i = 0; i < WorkloadRecords; i++) {
      if (i == WorkloadRecords / 2) {
        db.CreateCollection<Tag>($"Tag{i}");
      }
      entities.Insert(TestPeople.Numbered(BaselineRecords + i));
    }
  }
}
