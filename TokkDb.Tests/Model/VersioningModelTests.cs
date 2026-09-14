using System.Diagnostics;
using System.Text;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests.Model;

//NF-6, WV-10 and V-15. Seeded random sequences of inserts, nested and array updates (some
//keyed), deletes, restores to random versions, purges of the collection and of one record,
//renames, retypes, removals, Rewrite and erasures, over at least 50 records, against an
//in-memory model of every version's document, parent, kind and operation. After every operation
//the engine's history is compared with the model's — every version through the current schema
//with what could not be mapped, the forest shape, and the as-of answers for sampled moments —
//and WV-10's invariants are checked by the store's own scan. V-15's invariant is what the
//comparison after a purge amounts to: the model's answers for every moment at or after the
//purge moment are the answers it had before. The kill runs repeat a shorter sequence, killing
//the process at 100 random writes, and hold history to the model at the last committed
//transaction. A failure prints the seed and the operation.
public class VersioningModelTests(ITestOutputHelper output) {
  private const string Collection = "Model";
  private const int Records = 50;

  private static int Operations => int.TryParse(Environment.GetEnvironmentVariable("TOKKDB_MODEL_OPS"), out var ops) ? ops : 1000;
  private static int KillPoints => int.TryParse(Environment.GetEnvironmentVariable("TOKKDB_MODEL_KILLS"), out var kills) ? kills : 100;

  [Fact]
  public void AThousandOperationsAgreeWithTheModelAfterEveryOne() {
    const int seed = 20260914;
    var watch = Stopwatch.StartNew();
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var model = new Model(seed);
    model.Create(db);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    for (var step = 0; step < Operations; step++) {
      var operation = model.NextOperation();
      try {
        model.Run(db, docs, operation);
        Assert.Equal(model.Expected(), Actual(db, docs, model));
        var invariants = db.Versions.Verify(Collection);
        Assert.True(invariants.IsSound, invariants.ToString());
        if (step % 100 == 99) {
          var deep = db.VerifyHistory(Collection);
          Assert.True(deep.IsSound, deep.ToString());
          AssertStoredVersions(docs, model);
        }
      } catch (Exception exception) when (exception is not OutOfMemoryException) {
        throw new Xunit.Sdk.XunitException($"seed {seed}, operation {step} ({operation}): {exception.Message}", exception);
      }
    }
    var final = db.VerifyHistory(Collection);
    Assert.True(final.IsSound, final.ToString());
    AssertStoredVersions(docs, model);
    output.WriteLine($"{Operations} operations over {model.RecordIds.Count} records ({model.Counts}) in {watch.Elapsed.TotalSeconds:F1} s; " +
      $"{db.HistoryReport(Collection)}");
  }

  //Each kill point replays the same seeded sequence on a fresh file until the fault fires,
  //then reopens — recovery runs — and holds the history to the model before the interrupted
  //operation, or after it when its commit record was written first; a collection purge, one
  //transaction per record, may also have stopped between two records.
  [Fact]
  public void AHundredKillPointsLeaveHistoryEqualToTheModelAtTheLastCommittedOperation() {
    const int seed = 20260915;
    const int operations = 120;
    var watch = Stopwatch.StartNew();
    int writes;
    using (var file = new TempDatabaseFile()) {
      var disk = new FaultInjectingDiskManager(file.Path);
      using var db = new TokkDbConnection(disk);
      db.Load();
      var model = new Model(seed);
      model.Create(db);
      var docs = db.Entities(new FieldMapSerializer(), Collection);
      for (var step = 0; step < operations; step++) {
        model.Run(db, docs, model.NextOperation());
      }
      Assert.Equal(model.Expected(), Actual(db, docs, model));
      writes = disk.WriteCount;
    }
    var random = new Random(seed);
    var killPoints = Enumerable.Range(0, KillPoints).Select(_ => random.Next(1, writes + 1)).ToList();
    var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    Parallel.ForEach(killPoints, new ParallelOptions { MaxDegreeOfParallelism = 4 }, killAfter => {
      var outcome = RunKilled(seed, operations, killAfter);
      outcomes.AddOrUpdate(outcome, 1, (_, count) => count + 1);
    });
    foreach (var (outcome, count) in outcomes.OrderBy(pair => pair.Key)) {
      output.WriteLine($"{outcome}: {count}");
    }
    Assert.True(outcomes.Keys.Any(outcome => outcome.StartsWith("interrupted", StringComparison.Ordinal)), "no kill fired");
    output.WriteLine($"{KillPoints} kill points over {writes} writes in {watch.Elapsed.TotalSeconds:F1} s");
  }

  private static string RunKilled(int seed, int operations, int killAfter) {
    using var file = new TempDatabaseFile();
    var model = new Model(seed);
    var disk = new FaultInjectingDiskManager(file.Path, killAfter);
    var db = new TokkDbConnection(disk);
    Operation interrupted = null;
    string before = null;
    string firedAt = null;
    try {
      db.Load();
      model.Create(db);
      var docs = db.Entities(new FieldMapSerializer(), Collection);
      for (var step = 0; step < operations; step++) {
        var operation = model.NextOperation();
        before = model.Expected();
        try {
          model.Run(db, docs, operation);
        } catch (SimulatedProcessKillException kill) {
          interrupted = operation;
          firedAt = kill.Step;
          break;
        }
      }
    } catch (SimulatedProcessKillException kill) {
      //Killed while the collection was being made: nothing of the model exists yet.
      return $"interrupted before the first operation ({kill.Step})";
    } finally {
      db.Dispose();
    }
    if (interrupted is null) {
      return "ran to the end";
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var reopenedDocs = reopened.Entities(new FieldMapSerializer(), Collection);
    var invariants = reopened.Versions.Verify(Collection);
    Assert.True(invariants.IsSound, $"seed {seed}, kill after write {killAfter} ({firedAt}): {invariants}");
    //The engine's listing is read against the model's sampled moments, which move with the
    //model's versions, so it is read again after each step the model catches up by.
    var actual = Actual(reopened, reopenedDocs, model);
    if (actual == before) {
      return $"interrupted: {interrupted.Kind} rolled back ({reopened.RecoveryDecision.Outcome})";
    }
    //The commit record was written before the kill: the model catches up with the identifiers
    //the engine minted, one transaction at a time for a collection purge.
    foreach (var after in model.CatchUp(reopened, reopenedDocs, interrupted)) {
      actual = Actual(reopened, reopenedDocs, model);
      if (after == actual) {
        return $"interrupted: {interrupted.Kind} committed ({reopened.RecoveryDecision.Outcome})";
      }
    }
    throw new Xunit.Sdk.XunitException(
      $"seed {seed}, kill after write {killAfter} ({firedAt}) during {interrupted}: history matches neither the model before " +
      $"the operation nor after it.\nActual:\n{actual}\nBefore:\n{before}");
  }

  //Every version as stored, against the model: exact bytes at the schema version it was written
  //under — unless a Rewrite migrated the image while the version was the head, which WV-8
  //allows when the migration loses nothing; the stored image is then at the later schema and
  //is compared through the current one, where it must read the same.
  private static void AssertStoredVersions(DbEntities<Dictionary<string, IDocumentValue>> docs, Model model) {
    foreach (var record in model.Live) {
      foreach (var version in record.Versions.Where(version => version.Kind != VersionKind.Delete)) {
        var stored = docs.GetStoredAsOf(record.Id, version.Id);
        var expected = model.Serialize(version.Document, record.Id);
        if (stored.SchemaVersion == version.Schema) {
          Assert.True(CanonicalValue.Equal(expected.Value, stored.Document.Value), $"version {version.Id} of {record.Id} as stored");
        } else {
          Assert.True(stored.SchemaVersion > version.Schema, $"version {version.Id} of {record.Id} is stored at an older schema than it was written under");
          var mappedStored = SchemaMapping.MapDocument(stored.Document, model.Steps(stored.SchemaVersion, model.Schema)).Document;
          var mappedExpected = SchemaMapping.MapDocument(expected, model.Steps(version.Schema, model.Schema)).Document;
          Assert.True(CanonicalValue.Equal(mappedExpected.Value, mappedStored.Value), $"version {version.Id} of {record.Id} as stored, migrated by Rewrite");
        }
      }
    }
  }

  //What the engine says, in the model's format: every record's history and versions through the
  //current schema, and the answers for the sampled moments.
  private static string Actual(TokkDbConnection db, DbEntities<Dictionary<string, IDocumentValue>> docs, Model model) {
    var lines = new List<string>();
    var live = docs.GetAllRecords().Select(record => record.RecordId).ToHashSet();
    foreach (var recordId in model.RecordIds) {
      var history = docs.History(recordId);
      lines.Add($"record {recordId} live={live.Contains(recordId)} deleted={history.IsDeleted} head={history.Head} " +
        $"leaves={string.Join(",", history.Leaves.Order())} roots={string.Join(",", history.Roots.Order())}");
      foreach (var version in history.Versions) {
        var read = docs.GetAsOf(recordId, version.VersionId);
        var value = read.IsDeleted ? "deleted" : model.Bytes(read.Value, recordId);
        lines.Add($"  {version.VersionId} {version.Kind} parent={version.Parent} cut={version.CutFrom} replaced={version.ReplacedHead} " +
          $"{value} unmapped={Model.Describe(read.Unmapped)}");
      }
      foreach (var moment in model.Moments(recordId)) {
        var result = docs.GetAsOf(recordId, moment);
        lines.Add($"  asof {moment:O} {result.Outcome} {result.VersionId} " +
          (result.IsFound ? model.Bytes(result.Version.Value, recordId) : ""));
      }
    }
    return string.Join("\n", lines);
  }

  //The operations the generator draws, and what each one takes.
  private sealed record Operation(string Kind, int Record = -1, int Version = -1, int Moment = -1, int Choice = -1) {
    public override string ToString() => $"{Kind} record={Record} version={Version} moment={Moment} choice={Choice}";
  }

  private sealed class ModelVersion {
    public Ulid Id;
    public VersionKind Kind;
    public Ulid? Parent;
    public Ulid? CutFrom;
    public Ulid? ReplacedHead;
    public Dictionary<string, IDocumentValue> Document;
    public ushort Schema;
  }

  private sealed class ModelRecord {
    public Ulid Id;
    public List<ModelVersion> Versions = [];
    public bool Erased;
    public ModelVersion Head => Versions.Count == 0 ? null : Versions[^1];
    public bool IsLive => !Erased && Head is { Kind: not VersionKind.Delete };
    public bool HasHistory => !Erased && Versions.Count > 0;
  }

  private sealed class Model(int seed) {
    private readonly Random _random = new(seed);
    private readonly List<ModelRecord> _records = [];
    private readonly List<(ushort NewSchema, ColumnMigration Step)> _migrations = [];
    private readonly FieldMapSerializer _serializer = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _counter;

    //The columns as they stand: Count and Note are the ones the schema changes touch.
    private readonly List<(string Name, ValueTypeEnum Type)> _columns = [
      ("Title", ValueTypeEnum.String), ("Count", ValueTypeEnum.Int), ("Note", ValueTypeEnum.String),
      ("Passport", ValueTypeEnum.Object), ("Tags", ValueTypeEnum.Array), ("Body", ValueTypeEnum.String)
    ];

    public ushort Schema { get; private set; } = 1;
    public IReadOnlyList<Ulid> RecordIds => _records.Select(record => record.Id).ToList();
    public IEnumerable<ModelRecord> Live => _records.Where(record => record.HasHistory);
    public string Counts => string.Join(", ", _counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}"));

    public void Create(TokkDbConnection db) {
      db.CreateCollection(Collection, Columns());
      db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, 6, 0.6);
      Schema = db.Collection(Collection).SchemaVersion;
    }

    private List<ColumnDescriptor> Columns() {
      return _columns.Select(column => new ColumnDescriptor(column.Name, column.Type,
        elementKey: column.Name == "Tags" ? "Name" : "")).ToList();
    }

    public IEnumerable<ColumnMigration> Steps(ushort from, ushort to) {
      return _migrations.Where(entry => entry.NewSchema > from && entry.NewSchema <= to).Select(entry => entry.Step);
    }

    public ObjectDocument Serialize(Dictionary<string, IDocumentValue> document, Ulid id) {
      return _serializer.Create(document, id);
    }

    public string Bytes(Dictionary<string, IDocumentValue> document, Ulid id) {
      return Convert.ToHexString(CanonicalValue.Bytes(Serialize(document, id).Value));
    }

    public static string Describe(IReadOnlyList<Unmapped> unmapped) {
      return string.Join(",", unmapped.Select(item => $"{item.Reason}:{item.Column}").Order());
    }

    private Dictionary<string, IDocumentValue> Mapped(ModelVersion version, Ulid id, out IReadOnlyList<Unmapped> unmapped) {
      var mapped = SchemaMapping.MapDocument(Serialize(version.Document, id), Steps(version.Schema, Schema));
      unmapped = mapped.Unmapped;
      return ((ObjectDocumentValue)mapped.Document.Value).Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    //Three moments per record, each a function of the record so that the model and the engine
    //are asked the same: before its first version, at the version half way through its
    //history, and a day after its last.
    public IEnumerable<DateTimeOffset> Moments(Ulid recordId) {
      var record = _records.First(candidate => candidate.Id == recordId);
      if (record.Versions.Count == 0) {
        yield return recordId.Time.AddDays(1);
        yield break;
      }
      yield return record.Versions[0].Id.Time.AddMilliseconds(-1);
      yield return record.Versions[record.Versions.Count / 2].Id.Time;
      yield return record.Versions[^1].Id.Time.AddDays(1);
    }

    //RH-3 by the model: the floor among the versions kept, and the four reasons when none.
    private string AsOf(ModelRecord record, DateTimeOffset moment) {
      var cutoff = LogicalTime.AtOrBefore(moment);
      var node = record.Versions.Where(version => version.Id.CompareTo(cutoff) <= 0).MaxBy(version => version.Id);
      if (node is null) {
        if (record.Erased || record.Versions.Count == 0) {
          return $"{AsOfOutcome.NoSuchRecord}  ";
        }
        var root = record.Versions[0];
        var outcome = root.Kind == VersionKind.Baseline || root.CutFrom is not null ? AsOfOutcome.BeforeRecordedHistory : AsOfOutcome.NotYetCreated;
        return $"{outcome}  ";
      }
      if (node.Kind == VersionKind.Delete) {
        return $"{AsOfOutcome.Deleted} {node.Id} ";
      }
      return $"{AsOfOutcome.Found} {node.Id} {Bytes(Mapped(node, record.Id, out _), record.Id)}";
    }

    public string Expected() {
      var lines = new List<string>();
      foreach (var record in _records) {
        var parents = record.Versions.Where(version => version.Parent is not null).Select(version => version.Parent!.Value).ToHashSet();
        var head = record.Head;
        lines.Add($"record {record.Id} live={record.IsLive} deleted={head is { Kind: VersionKind.Delete }} head={(head is null ? "" : head.Id.ToString())} " +
          $"leaves={string.Join(",", record.Versions.Where(version => !parents.Contains(version.Id)).Select(version => version.Id).Order())} " +
          $"roots={string.Join(",", record.Versions.Where(version => version.Parent is null).Select(version => version.Id).Order())}");
        foreach (var version in record.Versions) {
          var value = version.Kind == VersionKind.Delete ? "deleted" : Bytes(Mapped(version, record.Id, out var unmapped), record.Id);
          var described = version.Kind == VersionKind.Delete ? "" : Describe(Mapped(version, record.Id, out var again) is not null ? again : []);
          lines.Add($"  {version.Id} {version.Kind} parent={version.Parent} cut={version.CutFrom} replaced={version.ReplacedHead} {value} unmapped={described}");
        }
        foreach (var moment in Moments(record.Id)) {
          lines.Add($"  asof {moment:O} {AsOf(record, moment)}");
        }
      }
      return string.Join("\n", lines);
    }

    public Operation NextOperation() {
      var roll = _random.Next(1000);
      var live = _records.Where(record => record.IsLive).ToList();
      if (_records.Count < Records || roll < 20) {
        return new Operation("insert");
      }
      if (roll < 400 && live.Count > 0) {
        return new Operation("update", Record: _records.IndexOf(live[_random.Next(live.Count)]), Choice: _random.Next(8));
      }
      if (roll < 480 && live.Count > 5) {
        return new Operation("delete", Record: _records.IndexOf(live[_random.Next(live.Count)]));
      }
      if (roll < 620) {
        var restorable = _records.Where(record => record.HasHistory
          && record.Versions.Any(version => version.Kind != VersionKind.Delete && version != record.Head)).ToList();
        if (restorable.Count > 0) {
          var record = restorable[_random.Next(restorable.Count)];
          var candidates = record.Versions.Where(version => version.Kind != VersionKind.Delete && version != record.Head).ToList();
          return new Operation("restore", Record: _records.IndexOf(record), Version: record.Versions.IndexOf(candidates[_random.Next(candidates.Count)]));
        }
      }
      if (roll < 650) {
        return new Operation("purge", Moment: _random.Next(1000));
      }
      if (roll < 720) {
        var withHistory = _records.Where(record => record.Versions.Count > 1).ToList();
        if (withHistory.Count > 0) {
          return new Operation("purge-record", Record: _records.IndexOf(withHistory[_random.Next(withHistory.Count)]), Moment: _random.Next(1000));
        }
      }
      if (roll < 740) {
        return new Operation("rename");
      }
      if (roll < 760) {
        return new Operation("retype-count");
      }
      if (roll < 780) {
        return new Operation("retype-note");
      }
      if (roll < 795) {
        return new Operation("remove-or-add-note");
      }
      if (roll < 815) {
        return new Operation("rewrite");
      }
      if (roll < 835 && live.Count > 10) {
        return new Operation("erase", Record: _records.IndexOf(live[_random.Next(live.Count)]));
      }
      return live.Count > 0
        ? new Operation("update", Record: _records.IndexOf(live[_random.Next(live.Count)]), Choice: _random.Next(8))
        : new Operation("insert");
    }

    //Runs the operation against the engine and applies it to the model, with the identifiers the
    //engine minted. Returns after both.
    public void Run(TokkDbConnection db, DbEntities<Dictionary<string, IDocumentValue>> docs, Operation operation) {
      _counts[operation.Kind] = _counts.GetValueOrDefault(operation.Kind) + 1;
      switch (operation.Kind) {
        case "insert": {
          var document = Generate();
          var id = docs.Insert(document);
          ApplyInsert(id, docs.HeadVersion(id), document);
          break;
        }
        case "update": {
          var record = _records[operation.Record];
          var document = Mutate(record, operation.Choice);
          docs.Update(record.Id, document);
          ApplyUpdate(record, docs.HeadVersion(record.Id), document);
          break;
        }
        case "delete": {
          var record = _records[operation.Record];
          docs.Delete(record.Id);
          ApplyDelete(record, docs.HeadVersion(record.Id));
          break;
        }
        case "restore": {
          var record = _records[operation.Record];
          var target = record.Versions[operation.Version];
          var result = docs.Restore(record.Id, target.Id);
          var expectedUnmapped = ApplyRestore(record, target, result.NewVersion);
          Assert.Equal(Describe(expectedUnmapped), Describe(result.Unmapped));
          break;
        }
        case "purge": {
          var moment = PurgeMoment(operation.Moment);
          var report = db.PurgeHistory(Collection, moment);
          Assert.True(report.Succeeded, report.ToString());
          foreach (var record in _records) {
            ApplyPurge(record, moment);
          }
          break;
        }
        case "purge-record": {
          var record = _records[operation.Record];
          var moment = PurgeMoment(operation.Moment);
          var report = docs.PurgeHistory(record.Id, moment);
          Assert.True(report.Succeeded, report.ToString());
          ApplyPurge(record, moment);
          break;
        }
        case "rename": {
          var (from, to) = _columns.Any(column => column.Name == "Count") ? ("Count", "Number") : ("Number", "Count");
          ChangeSchema(db, ColumnMigration.Rename(Schema, from, to), columns => {
            var index = columns.FindIndex(column => column.Name == from);
            columns[index] = (to, columns[index].Type);
          });
          break;
        }
        case "retype-count": {
          var name = _columns.Any(column => column.Name == "Count") ? "Count" : "Number";
          var current = _columns.First(column => column.Name == name).Type;
          var newType = current == ValueTypeEnum.Int ? ValueTypeEnum.String : ValueTypeEnum.Int;
          ChangeSchema(db, ColumnMigration.Retype(Schema, name, newType), columns => {
            var index = columns.FindIndex(column => column.Name == name);
            columns[index] = (name, newType);
          });
          break;
        }
        case "retype-note": {
          if (!_columns.Any(column => column.Name == "Note")) {
            goto case "remove-or-add-note";
          }
          var current = _columns.First(column => column.Name == "Note").Type;
          var newType = current == ValueTypeEnum.String ? ValueTypeEnum.Int : ValueTypeEnum.String;
          ChangeSchema(db, ColumnMigration.Retype(Schema, "Note", newType), columns => {
            var index = columns.FindIndex(column => column.Name == "Note");
            columns[index] = ("Note", newType);
          });
          break;
        }
        case "remove-or-add-note": {
          if (_columns.Any(column => column.Name == "Note")) {
            ChangeSchema(db, ColumnMigration.Remove(Schema, "Note"), columns => columns.RemoveAll(column => column.Name == "Note"));
          } else {
            ChangeSchema(db, null, columns => columns.Add(("Note", ValueTypeEnum.String)));
          }
          break;
        }
        case "rewrite":
          db.Rewrite(Collection);
          break;
        case "erase": {
          var record = _records[operation.Record];
          docs.Erase(record.Id);
          ApplyErase(record);
          break;
        }
        default:
          throw new InvalidOperationException(operation.Kind);
      }
    }

    //After a kill during the operation: the model's states after it, with the identifiers the
    //reopened engine holds, one per transaction the operation ran.
    public IEnumerable<string> CatchUp(TokkDbConnection db, DbEntities<Dictionary<string, IDocumentValue>> docs, Operation operation) {
      switch (operation.Kind) {
        case "insert": {
          var known = _records.Select(record => record.Id).ToHashSet();
          var added = docs.GetAllRecords().Select(record => record.RecordId).Where(id => !known.Contains(id)).ToList();
          if (added.Count != 1) {
            yield break;
          }
          //The document is the one the generator produced: replayed from the same random state.
          ApplyInsert(added[0], docs.HeadVersion(added[0]), _lastGenerated);
          yield return Expected();
          break;
        }
        case "update": {
          var record = _records[operation.Record];
          ApplyUpdate(record, docs.HeadVersion(record.Id), _lastGenerated);
          yield return Expected();
          break;
        }
        case "delete": {
          var record = _records[operation.Record];
          ApplyDelete(record, docs.HeadVersion(record.Id));
          yield return Expected();
          break;
        }
        case "restore": {
          var record = _records[operation.Record];
          ApplyRestore(record, record.Versions[operation.Version], docs.HeadVersion(record.Id));
          yield return Expected();
          break;
        }
        case "purge": {
          var moment = PurgeMoment(operation.Moment);
          foreach (var record in _records.OrderBy(record => record.Id)) {
            ApplyPurge(record, moment);
            yield return Expected();
          }
          break;
        }
        case "purge-record": {
          ApplyPurge(_records[operation.Record], PurgeMoment(operation.Moment));
          yield return Expected();
          break;
        }
        case "rename":
        case "retype-count":
        case "retype-note":
        case "remove-or-add-note":
          //A schema change that committed shows in the catalogue; the model takes it from there.
          if (db.Collection(Collection).SchemaVersion > Schema) {
            ApplySchema(db);
            yield return Expected();
          }
          break;
        case "rewrite":
          yield return Expected();
          break;
        case "erase":
          ApplyErase(_records[operation.Record]);
          yield return Expected();
          break;
      }
    }

    private Dictionary<string, IDocumentValue> _lastGenerated;
    private ColumnMigration _pendingStep;
    private Action<List<(string Name, ValueTypeEnum Type)>> _pendingChange;

    private void ChangeSchema(TokkDbConnection db, ColumnMigration step, Action<List<(string Name, ValueTypeEnum Type)>> change) {
      _pendingStep = step;
      _pendingChange = change;
      var columns = _columns.ToList();
      change(columns);
      db.SetColumns(Collection, columns.Select(column => new ColumnDescriptor(column.Name, column.Type,
        elementKey: column.Name == "Tags" ? "Name" : "")).ToList(), step is null ? null : [step]);
      ApplySchema(db);
    }

    private void ApplySchema(TokkDbConnection db) {
      _pendingChange(_columns);
      Schema = db.Collection(Collection).SchemaVersion;
      if (_pendingStep is not null) {
        _migrations.Add((Schema, _pendingStep));
      }
      _pendingStep = null;
      _pendingChange = null;
    }

    private DateTimeOffset PurgeMoment(int choice) {
      var versions = _records.SelectMany(record => record.Versions).ToList();
      if (versions.Count == 0) {
        return DateTimeOffset.UtcNow;
      }
      return versions[choice % versions.Count].Id.Time;
    }

    private void ApplyInsert(Ulid id, Ulid versionId, Dictionary<string, IDocumentValue> document) {
      _records.Add(new ModelRecord {
        Id = id, Versions = [new ModelVersion { Id = versionId, Kind = VersionKind.Insert, Document = document, Schema = Schema }]
      });
    }

    private void ApplyUpdate(ModelRecord record, Ulid versionId, Dictionary<string, IDocumentValue> document) {
      record.Versions.Add(new ModelVersion {
        Id = versionId, Kind = VersionKind.Update, Parent = record.Head.Id, Document = document, Schema = Schema
      });
    }

    private void ApplyDelete(ModelRecord record, Ulid versionId) {
      record.Versions.Add(new ModelVersion { Id = versionId, Kind = VersionKind.Delete, Parent = record.Head.Id, Schema = record.Head.Schema });
    }

    private IReadOnlyList<Unmapped> ApplyRestore(ModelRecord record, ModelVersion target, Ulid versionId) {
      var document = Mapped(target, record.Id, out var unmapped);
      record.Versions.Add(new ModelVersion {
        Id = versionId, Kind = VersionKind.Restore, Parent = target.Id, ReplacedHead = record.Head.Id, Document = document, Schema = Schema
      });
      return unmapped;
    }

    private void ApplyErase(ModelRecord record) {
      record.Versions.Clear();
      record.Erased = true;
    }

    //V-15's four steps on the model.
    private static void ApplyPurge(ModelRecord record, DateTimeOffset moment) {
      if (record.Versions.Count == 0) {
        return;
      }
      var cutoff = LogicalTime.AtOrBefore(moment);
      var headAtMoment = record.Versions.Where(version => version.Id.CompareTo(cutoff) <= 0).MaxBy(version => version.Id);
      var kept = record.Versions.Where(version => version.Id.CompareTo(cutoff) > 0 || version == headAtMoment).ToList();
      var keptIds = kept.Select(version => version.Id).ToHashSet();
      foreach (var version in kept) {
        if (version.Parent is { } parent && !keptIds.Contains(parent)) {
          version.CutFrom = parent;
          version.Parent = null;
        }
      }
      record.Versions = kept;
    }

    //A document with every current column, values of the declared types.
    private Dictionary<string, IDocumentValue> Generate() {
      var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
      foreach (var (name, type) in _columns) {
        document[name] = Value(name, type);
      }
      _lastGenerated = document;
      return document;
    }

    private IDocumentValue Value(string name, ValueTypeEnum type) {
      var n = ++_counter;
      return name switch {
        "Title" => new StringDocumentValue($"T-{n}"),
        "Passport" => new ObjectDocumentValue(new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
          ["Code"] = new StringDocumentValue($"P-{n}"), ["Issued"] = new IntDocumentValue(2000 + n % 26)
        }),
        "Tags" => Tags(_random.Next(0, 5)),
        "Body" => new StringDocumentValue(new string((char)('a' + n % 26), 40 + _random.Next(200))),
        _ when type == ValueTypeEnum.Int => new IntDocumentValue(n),
        //Note: numeric text half the time, so that a retype to Int is sometimes lossless.
        _ when name == "Note" && _random.Next(2) == 0 => new StringDocumentValue(n.ToString()),
        _ when name == "Note" => new StringDocumentValue($"note {n}"),
        _ => new StringDocumentValue(n.ToString())
      };
    }

    private ArrayDocumentValue Tags(int count) {
      return new ArrayDocumentValue(Enumerable.Range(0, count).Select(IDocumentValue (i) => new ObjectDocumentValue(
        new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
          ["Name"] = new StringDocumentValue($"t{i}"), ["Weight"] = new IntDocumentValue(_random.Next(100))
        })).ToArray());
    }

    //The head through the current schema, with one thing changed: a scalar, the nested
    //object, the keyed array — appended, removed, reordered or one element's weight — or all of it.
    private Dictionary<string, IDocumentValue> Mutate(ModelRecord record, int choice) {
      var document = Mapped(record.Head, record.Id, out _);
      //Every current column present, whatever the mapping could not carry.
      foreach (var (name, type) in _columns) {
        if (!document.ContainsKey(name) || document[name] is NullDocumentValue) {
          document[name] = Value(name, type);
        }
      }
      foreach (var stale in document.Keys.Where(key => _columns.All(column => column.Name != key)).ToList()) {
        document.Remove(stale);
      }
      var n = ++_counter;
      switch (choice) {
        case 0:
          document["Title"] = new StringDocumentValue($"T-{n}");
          break;
        case 1: {
          var (name, type) = _columns.First(column => column.Name is "Count" or "Number");
          document[name] = Value(name, type);
          break;
        }
        case 2: {
          var passport = ((ObjectDocumentValue)document["Passport"]).Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
          passport["Code"] = new StringDocumentValue($"P-{n}");
          document["Passport"] = new ObjectDocumentValue(passport);
          break;
        }
        case 3: {
          var tags = ((ArrayDocumentValue)document["Tags"]).Values.ToList();
          tags.Add(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
            ["Name"] = new StringDocumentValue($"t{n}"), ["Weight"] = new IntDocumentValue(n % 100)
          }));
          document["Tags"] = new ArrayDocumentValue(tags.ToArray());
          break;
        }
        case 4: {
          var tags = ((ArrayDocumentValue)document["Tags"]).Values.ToList();
          if (tags.Count > 0) {
            tags.RemoveAt(_random.Next(tags.Count));
            document["Tags"] = new ArrayDocumentValue(tags.ToArray());
          } else {
            document["Body"] = new StringDocumentValue($"body {n}");
          }
          break;
        }
        case 5: {
          var tags = ((ArrayDocumentValue)document["Tags"]).Values.ToList();
          if (tags.Count > 1) {
            tags.Reverse();
            var first = ((ObjectDocumentValue)tags[0]).Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            first["Weight"] = new IntDocumentValue(n % 100);
            tags[0] = new ObjectDocumentValue(first);
            document["Tags"] = new ArrayDocumentValue(tags.ToArray());
          } else {
            document["Body"] = new StringDocumentValue(((StringDocumentValue)document["Body"]).Value + $" {n}");
          }
          break;
        }
        case 6:
          document["Body"] = new StringDocumentValue(new string((char)('a' + n % 26), 40 + _random.Next(200)));
          break;
        default:
          document = Generate();
          break;
      }
      _lastGenerated = document;
      return document;
    }
  }
}
