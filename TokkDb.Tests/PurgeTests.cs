using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//RP-1, RP-2, RP-3, V-15, G-7, S-6 and N-3. The clock is stepped one second per reading so that
//every version has a logical time of its own and a moment can fall cleanly between two.
[Collection(EngineClockCollection.Name)]
public class PurgeTests(ITestOutputHelper output) {
  private const string Collection = "Doc";

  //Starts a day ahead of the wall clock, and forgets the identifiers an earlier test minted,
  //so that logical time is the stepped clock's and not a monotone correction of it (HS-7);
  //forgets its own again when disposed, so that the next test's clock is not behind them.
  private sealed class SteppingClock : IDisposable {
    public SteppingClock() {
      RecordIdentity.ResetForTests();
      Now = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero);
    }

    public void Dispose() {
      RecordIdentity.ResetForTests();
    }

    public DateTimeOffset Now { get; private set; }
    public DateTimeOffset Read() => Now = Now.AddSeconds(1);
    //A moment strictly between the last reading and the next.
    public DateTimeOffset Between => Now.AddMilliseconds(500);
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, int k = 8, double ratio = 0.5) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, [
      new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String),
      new ColumnDescriptor("N", ValueTypeEnum.Int), new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, k, ratio);
    return db;
  }

  private static Dictionary<string, IDocumentValue> Doc(string title, int n, string nField = "N", params string[] tags) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title),
      ["Body"] = new StringDocumentValue(new string('b', 300)),
      [nField] = new IntDocumentValue(n),
      ["Tags"] = new ArrayDocumentValue(tags.Select(IDocumentValue (tag) => new StringDocumentValue(tag)).ToArray())
    };
  }

  private static string Bytes(Dictionary<string, IDocumentValue> document, Ulid id) {
    return Convert.ToHexString(CanonicalValue.Bytes(new FieldMapSerializer().Create(document, id).Value));
  }

  //What a moment answers, as a string: the outcome, the version and the document.
  private static string Reading(DbEntities<Dictionary<string, IDocumentValue>> docs, Ulid id, DateTimeOffset moment) {
    var result = docs.GetAsOf(id, moment);
    return result.IsFound
      ? $"{result.Outcome}:{result.VersionId}:{Bytes(result.Version.Value, id)}:{result.Version.Unmapped.Count}"
      : $"{result.Outcome}:{result.VersionId}";
  }

  private static string Reading(DbEntities<Dictionary<string, IDocumentValue>> docs, Ulid id, Ulid version) {
    var read = docs.GetAsOf(id, version);
    return read.IsDeleted ? "deleted" : $"{Bytes(read.Value, id)}:{read.Unmapped.Count}";
  }

  //The shape of a record's forest, node by node.
  private static string Shape(TokkDbConnection db, Ulid id) {
    return string.Join("|", db.Versions.Nodes(Collection, id).Select(node =>
      $"{node.VersionId}:{node.Kind}:{node.Parent}:{node.Distance}:{node.CutFrom}:{(node.Image is null ? "-" : "image")}:{(node.Delta is null ? "-" : "delta")}"));
  }

  private static List<Ulid> Operations(TokkDbConnection db) {
    var history = HistoryCollections.NameFor(db.Collection(Collection).Id);
    return db.SystemDocuments.ReadAll(history)
      .Where(document => HistoryDocuments.TypeOf(document.Document) == HistoryDocuments.OperationType)
      .Select(document => document.Id)
      .Order()
      .ToList();
  }

  //S-6, and G-7 on it.
  [Fact]
  public void PurgeWithBranchesGivesAForestAndKeepsEveryAnswerAfterTheMoment() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var id = docs.Insert(Doc("v1", 1));
    var versions = new List<Ulid> { docs.HeadVersion(id) };
    docs.Update(id, Doc("v2", 2));
    versions.Add(docs.HeadVersion(id));
    docs.Update(id, Doc("v3", 3));
    versions.Add(docs.HeadVersion(id));
    var before = clock.Between;
    docs.Update(id, Doc("v4", 4));
    versions.Add(docs.HeadVersion(id));
    versions.Add(docs.Restore(id, versions[1]).NewVersion);
    docs.Update(id, Doc("v6", 6));
    versions.Add(docs.HeadVersion(id));
    var moments = new List<DateTimeOffset> { before, clock.Now.AddDays(1) };
    moments.AddRange(versions.Skip(3).SelectMany(version => new[] { version.Time, version.Time.AddMilliseconds(500) }));
    var answersBefore = moments.Select(moment => Reading(docs, id, moment)).ToList();
    var keptBefore = versions.Skip(2).ToDictionary(version => version, version => Reading(docs, id, version));

    var report = db.PurgeHistory(Collection, before);

    Assert.True(report.Succeeded, report.ToString());
    Assert.Equal(2, report.NodesRemoved);
    Assert.Equal(2, report.NodesRerooted);
    var history = docs.History(id);
    Assert.Equal([versions[2], versions[4]], history.Roots.Order());
    Assert.Equal(versions.Skip(2), history.Versions.Select(version => version.VersionId));
    foreach (var root in history.Versions.Where(version => version.Parent is null)) {
      Assert.Equal(versions[1], root.CutFrom);
      var node = db.Versions.Node(Collection, id, root.VersionId);
      Assert.Null(node.Delta);
      Assert.NotNull(node.Image);
    }
    Assert.Equal(answersBefore, moments.Select(moment => Reading(docs, id, moment)).ToList());
    foreach (var (version, reading) in keptBefore) {
      Assert.Equal(reading, Reading(docs, id, version));
    }
    Assert.Equal(AsOfOutcome.BeforeRecordedHistory, docs.GetAsOf(id, versions[0].Time).Outcome);
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());

    //N-3: a purged version is no longer kept.
    var refusal = Assert.Throws<RestoreRefusedException>(() => docs.Restore(id, versions[0]));
    Assert.Equal(RestoreRefusal.NoLongerKept, refusal.Reason);
    Assert.Equal(versions[5], docs.HeadVersion(id));
  }

  //RP-1's rerooting check: a record whose kept forest could not be rebuilt is rolled back,
  //reported, and left as it was — and the next purge, without the fault, finishes it.
  [Fact]
  public void ARecordThatCannotBeRebuiltIsRolledBackAndReported() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var id = docs.Insert(Doc("v1", 1));
    docs.Update(id, Doc("v2", 2));
    var second = docs.HeadVersion(id);
    var before = clock.Between;
    docs.Update(id, Doc("v3", 3));
    var other = docs.Insert(Doc("other", 9));
    var shapeBefore = Shape(db, id);
    var readingsBefore = docs.History(id).Versions.Select(version => Reading(docs, id, version.VersionId)).ToList();
    var store = (VersionStore)db.Versions;
    store.DropRerootedImageForTests = node => node.VersionId == second;

    var report = db.PurgeHistory(Collection, before);

    var failure = Assert.Single(report.Failures);
    Assert.Equal(id, failure.RecordId);
    Assert.Contains(second.ToString(), failure.Problem);
    Assert.Equal(0, report.RecordsChanged);
    Assert.Equal(shapeBefore, Shape(db, id));
    Assert.Equal(readingsBefore, docs.History(id).Versions.Select(version => Reading(docs, id, version.VersionId)).ToList());
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
    //The connection goes on working after the rollback: the other record, and a write.
    Assert.Equal(9, ((IntDocumentValue)docs.GetById(other).Value["N"]).Value);
    docs.Update(other, Doc("other", 10));

    store.DropRerootedImageForTests = null;
    var again = db.PurgeHistory(Collection, before);
    Assert.True(again.Succeeded, again.ToString());
    Assert.Equal(1, again.RecordsChanged);
    Assert.Equal(1, again.NodesRemoved);
    Assert.Equal(readingsBefore.Skip(1), docs.History(id).Versions.Select(version => Reading(docs, id, version.VersionId)));
    Assert.True(db.Versions.Verify(Collection).IsSound);
  }

  //RP-2 over generated histories: branches, restores of versions older than the moment,
  //deletes and schema changes.
  [Fact]
  public void GeneratedHistoriesAnswerTheSameAtAndAfterTheMomentAndEveryKeptVersionReadsTheSame() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file, k: 4);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var random = new Random(20260914);
    var records = new List<Ulid>();
    var deleted = new HashSet<Ulid>();
    var nField = "N";
    DateTimeOffset? before = null;
    const int operations = 160;
    for (var step = 0; step < operations; step++) {
      if (step == 96) {
        before = clock.Between;
      }
      if (step == 40) {
        db.SetColumns(Collection, [
          new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String),
          new ColumnDescriptor("Num", ValueTypeEnum.Int), new ColumnDescriptor("Tags", ValueTypeEnum.Array)
        ], [ColumnMigration.Rename(db.Collection(Collection).SchemaVersion, "N", "Num")]);
        nField = "Num";
        continue;
      }
      if (step == 120) {
        db.SetColumns(Collection, [
          new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String),
          new ColumnDescriptor("Num", ValueTypeEnum.Int), new ColumnDescriptor("Tags", ValueTypeEnum.Array),
          new ColumnDescriptor("Extra", ValueTypeEnum.String)
        ]);
        continue;
      }
      var roll = random.Next(100);
      if (records.Count < 12 && (records.Count == 0 || roll < 15)) {
        records.Add(docs.Insert(Doc($"r{records.Count}", step, nField, "a")));
        continue;
      }
      var id = records[random.Next(records.Count)];
      var history = docs.History(id);
      if (roll < 55 && !deleted.Contains(id)) {
        docs.Update(id, Doc($"r{step}", step, nField, random.Next(2) == 0 ? "a" : "b", $"t{step % 3}"));
      } else if (roll < 65 && !deleted.Contains(id)) {
        docs.Delete(id);
        deleted.Add(id);
      } else {
        //A restore: of a version older than the moment once the moment has passed, when the
        //record has one, so that the purge has forests to make.
        var candidates = history.Versions
          .Where(version => version.Kind != VersionKind.Delete && version.VersionId != history.Head
            && (before is null || version.LogicalTime < before))
          .ToList();
        if (candidates.Count == 0) {
          continue;
        }
        docs.Restore(id, candidates[random.Next(candidates.Count)].VersionId);
        deleted.Remove(id);
      }
    }
    Assert.NotNull(before);
    var moments = new List<DateTimeOffset> { before.Value, clock.Now.AddDays(1) };
    foreach (var id in records) {
      moments.AddRange(docs.History(id).Versions.Where(version => version.LogicalTime >= before.Value)
        .SelectMany(version => new[] { version.LogicalTime, version.LogicalTime.AddMilliseconds(500) }));
    }
    var answersBefore = records.ToDictionary(id => id, id => moments.Select(moment => Reading(docs, id, moment)).ToList());
    var versionsBefore = records.ToDictionary(id => id,
      id => docs.History(id).Versions.ToDictionary(version => version.VersionId, version => Reading(docs, id, version.VersionId)));
    var nodesBefore = records.Sum(id => docs.History(id).Versions.Count);

    var report = db.PurgeHistory(Collection, before.Value);

    output.WriteLine(report.ToString());
    Assert.True(report.Succeeded, report.ToString());
    Assert.True(report.NodesRemoved > 0);
    Assert.True(report.NodesRerooted > 0);
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
    var forests = 0;
    foreach (var id in records) {
      Assert.Equal(answersBefore[id], moments.Select(moment => Reading(docs, id, moment)).ToList());
      var history = docs.History(id);
      if (history.Roots.Count > 1) {
        forests++;
      }
      foreach (var version in history.Versions) {
        Assert.Equal(versionsBefore[id][version.VersionId], Reading(docs, id, version.VersionId));
        if (version.Kind == VersionKind.Delete) {
          continue;
        }
        var stored = docs.GetStoredAsOf(id, version.VersionId);
        Assert.True(stored.Report.VersionsExamined - 1 <= version.Distance,
          $"{id} {version.VersionId}: {stored.Report.VersionsExamined - 1} steps up, distance {version.Distance}");
      }
      foreach (var root in history.Versions.Where(version => version.Parent is null && version.CutFrom is not null)) {
        Assert.Null(db.Versions.Node(Collection, id, root.VersionId).Delta);
      }
    }
    Assert.True(forests > 0, "no record became a forest");
    Assert.Equal(nodesBefore - report.NodesRemoved, records.Sum(id => docs.History(id).Versions.Count));
  }

  //RP-1: an interrupted purge, run again, gives the same history as one uninterrupted run.
  [Fact]
  public void AnInterruptedPurgeRunAgainEqualsOneRun() {
    using var file = new TempDatabaseFile();
    var records = new List<Ulid>();
    DateTimeOffset before;
    using var clock = new SteppingClock();
    using (EngineClock.Override(clock.Read))
    using (var db = NewDatabase(file, k: 3)) {
      var docs = db.Entities(new FieldMapSerializer(), Collection);
      for (var i = 0; i < 6; i++) {
        records.Add(docs.Insert(Doc($"r{i}", i)));
      }
      foreach (var id in records) {
        docs.Update(id, Doc("second", 2));
        docs.Update(id, Doc("third", 3));
      }
      before = clock.Between;
      foreach (var id in records.Take(4)) {
        docs.Update(id, Doc("fourth", 4));
      }
      docs.Restore(records[0], docs.History(records[0]).Versions[1].VersionId);
      docs.Delete(records[1]);
      docs.Delete(records[5]);
    }
    var pristine = File.ReadAllBytes(file.Path);

    var writes = RunPurge(file, before, int.MaxValue, out var expected);
    Assert.True(writes > 10, $"only {writes} writes to aim at");
    var samples = Enumerable.Range(1, writes).Where(write => write % 5 == 1 || write == writes).ToList();
    foreach (var killAfter in samples) {
      Restore(file, pristine);
      var interrupted = new FaultInjectingDiskManager(file.Path, killAfter);
      var db = new TokkDbConnection(interrupted);
      try {
        db.Load();
        db.PurgeHistory(Collection, before);
      } catch (SimulatedProcessKillException) {
        //A killed process gets no further.
      } finally {
        db.Dispose();
      }
      RunPurge(file, before, int.MaxValue, out var afterRerun);
      Assert.Equal(expected, afterRerun);
    }
    output.WriteLine($"{samples.Count} kill points over {writes} writes");
  }

  private static int RunPurge(TempDatabaseFile file, DateTimeOffset before, int killAfter, out string outcome) {
    var disk = new FaultInjectingDiskManager(file.Path, killAfter);
    using var db = new TokkDbConnection(disk);
    db.Load();
    var report = db.PurgeHistory(Collection, before);
    Assert.True(report.Succeeded, report.ToString());
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var lines = new List<string>();
    foreach (var id in db.Versions.NextRecords(Collection, null, 1000)) {
      lines.Add($"{id}={Shape(db, id)}");
      foreach (var version in docs.History(id).Versions) {
        lines.Add($"  {version.VersionId}={Reading(docs, id, version.VersionId)}");
      }
    }
    lines.Add("operations=" + string.Join(",", Operations(db)));
    var verification = db.Versions.Verify(Collection);
    Assert.True(verification.IsSound, verification.ToString());
    outcome = string.Join("\n", lines);
    return disk.WriteCount;
  }

  private static void Restore(TempDatabaseFile file, byte[] contents) {
    File.WriteAllBytes(file.Path, contents);
    var journal = Journal.GetJournalPath(file.Path);
    if (File.Exists(journal)) {
      File.WriteAllBytes(journal, []);
    }
  }

  //RP-3: a per-record purge leaves every other record byte-identical and every operation
  //document in place; a collection-wide purge removes exactly the operations no node names.
  [Fact]
  public void PurgingOneRecordLeavesTheOthersAndTheOperationsAlone() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var records = new List<Ulid>();
    for (var i = 0; i < 4; i++) {
      records.Add(docs.Insert(Doc($"r{i}", i)));
    }
    foreach (var id in records) {
      docs.Update(id, Doc("second", 2));
    }
    var before = clock.Between;
    foreach (var id in records) {
      docs.Update(id, Doc("third", 3));
    }
    var operationsBefore = Operations(db);
    var others = records.Skip(1).ToDictionary(id => id, id => NodeBytes(db, id));

    var one = docs.PurgeHistory(records[0], before);

    Assert.True(one.Succeeded, one.ToString());
    Assert.Equal(1, one.NodesRemoved);
    Assert.Equal(0, one.OperationsRemoved);
    Assert.Equal(operationsBefore, Operations(db));
    foreach (var (id, bytes) in others) {
      Assert.Equal(bytes, NodeBytes(db, id));
    }
    Assert.Equal(2, docs.History(records[0]).Versions.Count);

    var all = db.PurgeHistory(Collection, before);

    Assert.True(all.Succeeded, all.ToString());
    Assert.Equal(3, all.NodesRemoved);
    //The four inserts were one operation each, and nothing names them any more; the second
    //versions are the heads at the moment and keep theirs.
    Assert.Equal(4, all.OperationsRemoved);
    var referenced = records.SelectMany(id => docs.History(id).Versions.Select(version => version.OperationId)).ToHashSet();
    Assert.Equal(referenced.Order(), Operations(db));
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
  }

  private static List<string> NodeBytes(TokkDbConnection db, Ulid id) {
    return db.Versions.Nodes(Collection, id)
      .Select(node => Convert.ToHexString(CanonicalValue.Bytes(HistoryDocuments.WriteNode(node).Value)))
      .ToList();
  }

  //RP-2's last clause and V-17: right after a purge commits, a value that existed only in
  //removed versions is in neither the database file nor its journal.
  [Fact]
  public void AValueOnlyInRemovedVersionsIsInNeitherFileAfterThePurge() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var id = docs.Insert(Doc("PURGED-first-title", 1));
    docs.Update(id, Doc("PURGED-second-title", 2));
    docs.Update(id, Doc("kept-third-title", 3));
    var before = clock.Between;
    docs.Update(id, Doc("kept-fourth-title", 4));
    var journal = Disk.Journal.GetJournalPath(file.Path);
    Assert.True(File.ReadAllBytes(file.Path).AsSpan().IndexOf("PURGED-first-title"u8) >= 0);

    var report = db.PurgeHistory(Collection, before);

    Assert.True(report.Succeeded, report.ToString());
    Assert.Equal(2, report.NodesRemoved);
    foreach (var value in new[] { "PURGED-first-title", "PURGED-second-title" }) {
      Assert.True(File.ReadAllBytes(file.Path).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(value)) < 0, $"'{value}' is still in the database file");
      Assert.True(!File.Exists(journal) || File.ReadAllBytes(journal).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(value)) < 0, $"'{value}' is still in the journal");
    }
    Assert.True(File.ReadAllBytes(file.Path).AsSpan().IndexOf("kept-third-title"u8) >= 0);
  }

  [Fact]
  public void APurgeRefusesToRunInsideAUnitOfWorkOrOnAnUnversionedCollection() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Assert.Throws<InvalidOperationException>(() => db.InTransaction(() => db.PurgeHistory(Collection, DateTimeOffset.UtcNow)));
    db.CreateCollection("Plain", [new ColumnDescriptor("Title", ValueTypeEnum.String)]);
    db.SetRetentionPolicy("Plain", RetentionPolicy.None, dropHistory: true);
    Assert.Throws<InvalidOperationException>(() => db.PurgeHistory("Plain", DateTimeOffset.UtcNow));
  }
}
