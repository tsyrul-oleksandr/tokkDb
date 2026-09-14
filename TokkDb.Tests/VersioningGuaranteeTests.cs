using System.Reflection;
using System.Text;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Tests.Architecture;
using TokkDb.Tests.Fixtures;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//§6: one test per guarantee, each phrased as the promise it checks, through the public surface
//of §3.2 only — no node, page or index inspection, which the last test enforces over this
//class's compiled code — so that they stay valid if the mechanism behind them changes.
[Collection(EngineClockCollection.Name)]
public class VersioningGuaranteeTests {
  private const string Notes = "Notes";

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, int k = 8, double ratio = 0.5) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Notes, [
      new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Count", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)
    ]);
    db.SetRetentionPolicy(Notes, RetentionPolicy.KeepVersions, k, ratio);
    return db;
  }

  private static Dictionary<string, IDocumentValue> Note(string title, int count = 1, string note = "n") {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Count"] = new IntDocumentValue(count),
      ["Note"] = new StringDocumentValue(note), ["Body"] = new StringDocumentValue(new string('b', 200))
    };
  }

  private static string Bytes(Dictionary<string, IDocumentValue> document, Ulid id) {
    return Convert.ToHexString(CanonicalValue.Bytes(new FieldMapSerializer().Create(document, id).Value));
  }

  private static string Reading(DbEntities<Dictionary<string, IDocumentValue>> docs, Ulid id, Ulid version) {
    var read = docs.GetAsOf(id, version);
    return read.IsDeleted ? "deleted" : $"{Bytes(read.Value, id)}|{string.Join(",", read.Unmapped.Select(item => $"{item.Reason}:{item.Column}").Order())}";
  }

  private static string Reading(DbEntities<Dictionary<string, IDocumentValue>> docs, Ulid id, DateTimeOffset moment) {
    var result = docs.GetAsOf(id, moment);
    return result.IsFound ? $"{result.Outcome}:{result.VersionId}:{Bytes(result.Version.Value, id)}" : $"{result.Outcome}:{result.VersionId}";
  }

  //G-1 — A unit of work is the boundary.
  [Fact]
  public void AUnitOfWorkIsTheBoundary() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Notes);
    var a = docs.Insert(Note("a"));
    var b = docs.Insert(Note("b"));

    //Everything a committed unit of work changed is one operation, and no reader sees it early.
    db.InTransaction(() => {
      docs.Update(a, Note("a2"));
      docs.Update(b, Note("b2"));
      docs.Insert(Note("c"));
      using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
      reader.Load();
      var readerDocs = reader.Entities(new FieldMapSerializer(), Notes);
      Assert.Equal("a", ((StringDocumentValue)readerDocs.GetById(a).Value["Title"]).Value);
      Assert.Equal(2, readerDocs.GetAll().Count());
      Assert.Single(readerDocs.History(a).Versions);
    });
    var operations = new[] { a, b }.SelectMany(id => docs.History(id).Versions.Skip(1)).Select(version => version.OperationId).ToHashSet();
    Assert.Single(operations);
    Assert.Equal(3, docs.GetAll().Count());

    //A rolled-back unit of work leaves nothing in history.
    var headsBefore = new[] { a, b }.Select(docs.HeadVersion).ToList();
    var countBefore = docs.History(a).Versions.Count + docs.History(b).Versions.Count;
    Assert.Throws<InvalidOperationException>(() => db.InTransaction(() => {
      docs.Update(a, Note("a3"));
      docs.Delete(b);
      throw new InvalidOperationException("abandon");
    }));
    Assert.Equal(headsBefore, new[] { a, b }.Select(docs.HeadVersion));
    Assert.Equal(countBefore, docs.History(a).Versions.Count + docs.History(b).Versions.Count);
    Assert.Equal("a2", ((StringDocumentValue)docs.GetById(a).Value["Title"]).Value);
    Assert.False(docs.History(b).IsDeleted);
  }

  //G-2 — History tells the truth.
  [Fact]
  public void HistoryTellsTheTruth() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Notes);
    var id = docs.Insert(Note("one", 1));
    var seen = new Dictionary<Ulid, string> { [docs.HeadVersion(id)] = Bytes(docs.GetById(id).Value, id) };
    foreach (var (title, count) in new[] { ("two", 2), ("three", 3) }) {
      docs.Update(id, Note(title, count));
      seen[docs.HeadVersion(id)] = Bytes(docs.GetById(id).Value, id);
    }
    //A write that changes nothing adds no version; one that changes something adds exactly one.
    var versions = docs.History(id).Versions.Count;
    docs.Update(id, Note("three", 3));
    Assert.Equal(versions, docs.History(id).Versions.Count);
    docs.Update(id, Note("four", 4));
    Assert.Equal(versions + 1, docs.History(id).Versions.Count);
    seen[docs.HeadVersion(id)] = Bytes(docs.GetById(id).Value, id);

    //As stored: exactly what GetById returned right after the write.
    foreach (var (version, bytes) in seen) {
      var stored = docs.GetStoredAsOf(id, version);
      Assert.Equal(bytes, Convert.ToHexString(CanonicalValue.Bytes(stored.Document.Value)));
    }
    //Through the current schema after a rename and a removal: the same document mapped by
    //V-10's rules, with what the mapping could not carry reported.
    db.SetColumns(Notes, [
      new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Number", ValueTypeEnum.Int),
      new ColumnDescriptor("Body", ValueTypeEnum.String)
    ], [ColumnMigration.Rename(db.Collection(Notes).SchemaVersion, "Count", "Number"), ColumnMigration.Remove(db.Collection(Notes).SchemaVersion, "Note")]);
    foreach (var version in seen.Keys) {
      var stored = docs.GetStoredAsOf(id, version);
      var read = docs.GetAsOf(id, version);
      Assert.Equal(((IntDocumentValue)stored.Document.Value.AsObject()["Count"]).Value, ((IntDocumentValue)read.Value["Number"]).Value);
      Assert.False(read.Value.ContainsKey("Note"));
      var unmapped = Assert.Single(read.Unmapped);
      Assert.Equal("Note", unmapped.Column);
      Assert.Equal("n", ((StringDocumentValue)unmapped.Value).Value);
    }
  }

  //G-3 — Time never runs backwards.
  [Fact]
  public void TimeNeverRunsBackwards() {
    RecordIdentity.ResetForTests();
    try {
      using var file = new TempDatabaseFile();
      using var db = NewDatabase(file);
      var docs = db.Entities(new FieldMapSerializer(), Notes);
      var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero);
      var now = start;
      var titles = new List<string>();
      Ulid id;
      using (EngineClock.Override(() => now)) {
        id = docs.Insert(Note("t0"));
        titles.Add("t0");
        var offsets = new[] { 1, -3600, -3599, 2, 3 };
        for (var i = 0; i < offsets.Length; i++) {
          now = start.AddSeconds(offsets[i]);
          docs.Update(id, Note($"t{i + 1}"));
          titles.Add($"t{i + 1}");
        }
      }
      //History lists versions in write order, whatever the clock said.
      var history = docs.History(id).Versions;
      Assert.Equal(titles, history.Select(version => ((StringDocumentValue)docs.GetAsOf(id, version.VersionId).Value["Title"]).Value));
      Assert.Equal(history.Select(version => version.VersionId).Order(), history.Select(version => version.VersionId));
      Assert.NotEmpty(docs.History(id).OperationsRecordedOutOfOrder);
      //GetAsOf is monotone in the moment: as the moment advances, the version found never goes back.
      var moments = history.Select(version => version.LogicalTime).Concat([start.AddHours(-2), start.AddDays(2)]).Order().ToList();
      Ulid? last = null;
      foreach (var moment in moments) {
        var result = docs.GetAsOf(id, moment);
        if (result.IsFound) {
          Assert.True(last is null || result.VersionId!.Value.CompareTo(last.Value) >= 0);
          last = result.VersionId;
        }
      }
      Assert.Equal(history[^1].VersionId, last);
    } finally {
      RecordIdentity.ResetForTests();
    }
  }

  //G-4 — Restore never destroys.
  [Fact]
  public void RestoreNeverDestroys() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Notes);
    var id = docs.Insert(Note("one", 1, "keep"));
    docs.Update(id, Note("two", 2, "keep"));
    docs.Update(id, Note("three", 3, "keep"));
    docs.Delete(id);
    var target = docs.History(id).Versions[1].VersionId;
    var before = docs.History(id).Versions.ToDictionary(version => version.VersionId, version => Reading(docs, id, version.VersionId));
    db.SetColumns(Notes, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Count", ValueTypeEnum.Int),
      new ColumnDescriptor("Body", ValueTypeEnum.String)], [ColumnMigration.Remove(db.Collection(Notes).SchemaVersion, "Note")]);
    var beforeMapped = docs.History(id).Versions.ToDictionary(version => version.VersionId, version => Reading(docs, id, version.VersionId));

    var result = docs.Restore(id, target);

    //Every version that could be read before still reads, unchanged.
    foreach (var (version, reading) in beforeMapped) {
      Assert.Equal(reading, Reading(docs, id, version));
    }
    Assert.Equal(before.Count + 1, docs.History(id).Versions.Count);
    //The restored record equals the version restored, except for what the restore reports.
    var restored = docs.GetById(id).Value;
    var wanted = docs.GetAsOf(id, target).Value;
    Assert.Equal(Bytes(wanted, id), Bytes(restored, id));
    var unmapped = Assert.Single(result.Unmapped);
    Assert.Equal("Note", unmapped.Column);
    Assert.Equal("keep", ((StringDocumentValue)unmapped.Value).Value);
  }

  //G-5 — Branches are first-class.
  [Fact]
  public void BranchesAreFirstClass() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Notes);
    var id = docs.Insert(Note("v1", 1));
    docs.Update(id, Note("v2", 2));
    var second = docs.HeadVersion(id);
    docs.Update(id, Note("v3", 3));
    docs.Update(id, Note("v4", 4));
    docs.Restore(id, second);
    docs.Update(id, Note("v5", 5));
    var history = docs.History(id);
    Assert.Equal(2, history.Leaves.Count);

    //Every leaf can be read and restored.
    foreach (var leaf in history.Leaves) {
      Assert.False(docs.GetAsOf(id, leaf).IsDeleted);
    }
    var otherLeaf = history.Leaves.Single(leaf => leaf != history.Head);
    var otherBranch = docs.History(id).Versions.Where(version => version.VersionId != history.Head).Select(version => version.VersionId)
      .ToDictionary(version => version, version => Reading(docs, id, version));
    docs.Restore(id, otherLeaf);
    Assert.Equal("v4", ((StringDocumentValue)docs.GetById(id).Value["Title"]).Value);
    //Restoring and editing one branch changed no version on the other, whose leaf is still a leaf.
    docs.Update(id, Note("v6", 6));
    foreach (var (version, reading) in otherBranch) {
      Assert.Equal(reading, Reading(docs, id, version));
    }
    var afterwards = docs.History(id);
    Assert.Equal(2, afterwards.Leaves.Count);
    Assert.Contains(history.Head!.Value, afterwards.Leaves);
    Assert.Equal("v6", ((StringDocumentValue)docs.GetById(id).Value["Title"]).Value);
  }

  //G-6 — Schema evolution never makes history unreadable, or silently lossy.
  [Fact]
  public void SchemaEvolutionNeverMakesHistoryUnreadableOrSilentlyLossy() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Notes);
    var random = new Random(6);
    var ids = new List<Ulid>();
    var columns = new List<(string Name, ValueTypeEnum Type)> {
      ("Title", ValueTypeEnum.String), ("Count", ValueTypeEnum.Int), ("Note", ValueTypeEnum.String), ("Body", ValueTypeEnum.String)
    };
    Dictionary<string, IDocumentValue> Generate(int n) {
      var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
      foreach (var (name, type) in columns) {
        document[name] = type == ValueTypeEnum.Int ? new IntDocumentValue(n) : new StringDocumentValue(name == "Note" && n % 2 == 0 ? n.ToString() : $"{name}-{n}");
      }
      return document;
    }
    void Change(ColumnMigration step, Action<List<(string Name, ValueTypeEnum Type)>> change) {
      change(columns);
      db.SetColumns(Notes, columns.Select(column => new ColumnDescriptor(column.Name, column.Type)).ToList(), step is null ? null : [step]);
    }
    for (var i = 0; i < 6; i++) {
      ids.Add(docs.Insert(Generate(i)));
    }
    for (var step = 0; step < 40; step++) {
      var roll = random.Next(10);
      var schema = db.Collection(Notes).SchemaVersion;
      if (roll < 5) {
        docs.Update(ids[random.Next(ids.Count)], Generate(100 + step));
      } else if (roll == 5) {
        var (from, to) = columns.Any(column => column.Name == "Count") ? ("Count", "Number") : ("Number", "Count");
        Change(ColumnMigration.Rename(schema, from, to), list => list[list.FindIndex(column => column.Name == from)] = (to, ValueTypeEnum.Int));
      } else if (roll == 6 && columns.Any(column => column.Name == "Note")) {
        var current = columns.First(column => column.Name == "Note").Type;
        var newType = current == ValueTypeEnum.String ? ValueTypeEnum.Int : ValueTypeEnum.String;
        Change(ColumnMigration.Retype(schema, "Note", newType), list => list[list.FindIndex(column => column.Name == "Note")] = ("Note", newType));
      } else if (roll == 7) {
        if (columns.Any(column => column.Name == "Note")) {
          Change(ColumnMigration.Remove(schema, "Note"), list => list.RemoveAll(column => column.Name == "Note"));
        } else {
          Change(null, list => list.Add(("Note", ValueTypeEnum.String)));
        }
      } else {
        db.Rewrite(Notes);
      }
      //After every change: every version reads, and everything the current schema cannot show
      //is reported as unmapped — and GetStoredAsOf still shows it.
      foreach (var id in ids) {
        foreach (var version in docs.History(id).Versions) {
          var read = docs.GetAsOf(id, version.VersionId);
          var stored = docs.GetStoredAsOf(id, version.VersionId);
          var storedFields = stored.Document.Value.AsObject();
          Assert.Equal(storedFields.Count, read.Value.Count + read.Unmapped.Count);
          foreach (var unmapped in read.Unmapped) {
            Assert.True(CanonicalValue.Equal(storedFields[unmapped.Column], unmapped.Original));
          }
        }
      }
    }
    Assert.True(db.Collection(Notes).SchemaVersion > 3, "the generator made too few schema changes");
  }

  //G-7 — Purge keeps the present and everything after the moment.
  [Fact]
  public void PurgeKeepsThePresentAndEverythingAfterTheMoment() {
    RecordIdentity.ResetForTests();
    try {
      using var file = new TempDatabaseFile();
      using var db = NewDatabase(file, k: 4);
      var docs = db.Entities(new FieldMapSerializer(), Notes);
      var now = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero);
      using var _ = EngineClock.Override(() => now = now.AddSeconds(1));
      var random = new Random(7);
      var ids = new List<Ulid>();
      for (var i = 0; i < 8; i++) {
        ids.Add(docs.Insert(Note($"r{i}", i)));
      }
      DateTimeOffset? before = null;
      for (var step = 0; step < 80; step++) {
        if (step == 45) {
          before = now.AddMilliseconds(500);
        }
        var id = ids[random.Next(ids.Count)];
        var history = docs.History(id);
        var roll = random.Next(10);
        if (roll < 6 && !history.IsDeleted) {
          docs.Update(id, Note($"s{step}", step));
        } else if (roll < 7 && !history.IsDeleted) {
          docs.Delete(id);
        } else {
          var candidates = history.Versions.Where(version => version.Kind != VersionKind.Delete && version.VersionId != history.Head).ToList();
          if (candidates.Count > 0) {
            docs.Restore(id, candidates[random.Next(candidates.Count)].VersionId);
          }
        }
      }
      var moments = new List<DateTimeOffset> { before!.Value, now.AddDays(1) };
      foreach (var id in ids) {
        moments.AddRange(docs.History(id).Versions.Where(version => version.LogicalTime >= before).Select(version => version.LogicalTime));
      }
      var answers = ids.ToDictionary(id => id, id => moments.Select(moment => Reading(docs, id, moment)).ToList());
      var after = ids.ToDictionary(id => id, id => docs.History(id).Versions.Where(version => version.LogicalTime >= before)
        .ToDictionary(version => version.VersionId, version => Reading(docs, id, version.VersionId)));

      var report = db.PurgeHistory(Notes, before.Value);

      Assert.True(report.Succeeded, report.ToString());
      Assert.True(report.NodesRemoved > 0);
      foreach (var id in ids) {
        Assert.Equal(answers[id], moments.Select(moment => Reading(docs, id, moment)).ToList());
        foreach (var (version, reading) in after[id]) {
          Assert.Equal(reading, Reading(docs, id, version));
        }
      }
    } finally {
      RecordIdentity.ResetForTests();
    }
  }

  //G-8 — Erase is total within its boundary.
  [Fact]
  public void EraseIsTotalWithinItsBoundary() {
    using var file = new TempDatabaseFile();
    var journal = Journal.GetJournalPath(file.Path);
    var values = new List<string>();
    using (var db = NewDatabase(file)) {
      db.CreateIndex(Notes, "Title");
      var docs = db.Entities(new FieldMapSerializer(), Notes);
      var id = docs.Insert(Note("ERASED-0", 0, "ERASED-note-0"));
      values.AddRange(["ERASED-0", "ERASED-note-0"]);
      for (var i = 1; i <= 3; i++) {
        docs.Update(id, Note($"ERASED-{i}", i, $"ERASED-note-{i}"));
        values.AddRange([$"ERASED-{i}", $"ERASED-note-{i}"]);
      }
      docs.Insert(Note("STAYS"));

      docs.Erase(id);

      //With the connection still open, and before any further transaction.
      foreach (var value in values) {
        Assert.False(Contains(file.Path, value), $"'{value}' is still in the database file");
        Assert.False(Contains(journal, value), $"'{value}' is still in the journal");
      }
      Assert.Equal(AsOfOutcome.NoSuchRecord, docs.GetAsOf(id, DateTimeOffset.UtcNow).Outcome);
    }
    //And after the connection is closed.
    foreach (var value in values) {
      Assert.False(Contains(file.Path, value));
      Assert.False(Contains(journal, value));
    }
    Assert.True(Contains(file.Path, "STAYS"));
  }

  private static bool Contains(string path, string needle) {
    return File.Exists(path) && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
  }

  //G-9 — Turning versioning on changes nothing already stored.
  [Fact]
  public void TurningVersioningOnChangesNothingAlreadyStored() {
    using var file = PreVersioningFixture.Copy();
    var before = File.ReadAllBytes(file.Path);
    List<(Ulid Id, int Amount, string Comment)> expenses;
    List<(Ulid Id, string Name)> conferences;
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      expenses = db.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses).GetAllRecords()
        .Select(record => (record.RecordId, record.Value.Amount, record.Value.Comment)).OrderBy(record => record.RecordId).ToList();
      conferences = db.Entities<PreVersioningFixture.Conference>(PreVersioningFixture.Conferences).GetAllRecords()
        .Select(record => (record.RecordId, record.Value.Name)).OrderBy(record => record.RecordId).ToList();

      db.SetRetentionPolicy(PreVersioningFixture.Expenses, RetentionPolicy.KeepVersions);
      db.SetRetentionPolicy(PreVersioningFixture.Conferences, RetentionPolicy.KeepVersions);

      //Every record reads the same afterwards, and none has a history yet.
      var expensesAfter = db.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses);
      Assert.Equal(expenses, expensesAfter.GetAllRecords().Select(record => (record.RecordId, record.Value.Amount, record.Value.Comment)).OrderBy(record => record.RecordId));
      Assert.Equal(conferences, db.Entities<PreVersioningFixture.Conference>(PreVersioningFixture.Conferences).GetAllRecords()
        .Select(record => (record.RecordId, record.Value.Name)).OrderBy(record => record.RecordId));
      Assert.All(expenses, expense => Assert.True(expensesAfter.History(expense.Id).IsEmpty));
    }
    //No page holding records changed: switching on rewrites the file's own first pages — the
    //root page, the catalogue and the free-space map, which a fresh database lays out before
    //any record page — and adds the history collections' pages beyond the old end of the file.
    //Every page in between, where the fixture's records lie, is byte-identical. (S-4's test in
    //VersionDeleteTests names the record pages through the engine; this one stays outside it.)
    var after = File.ReadAllBytes(file.Path);
    var pageSize = Configuration.TokkConstants.DefaultPageSize;
    var pages = before.Length / pageSize;
    var changed = Enumerable.Range(0, pages)
      .Where(page => !before.AsSpan(page * pageSize, pageSize).SequenceEqual(after.AsSpan(page * pageSize, pageSize)))
      .ToList();
    Assert.True(after.Length > before.Length, "the history collections should have added pages");
    Assert.True(pages > 8, "the fixture is too small to tell record pages from the file's own");
    Assert.True(changed.All(page => page < 4), $"pre-existing pages changed: {string.Join(",", changed)}");
  }

  //What the last test enforces: nothing above reaches past §3.2. The rule is checked over the
  //compiled class — lambdas and local functions included — so that a helper cannot slip past.
  private static readonly HashSet<Type> Forbidden = [
    typeof(IVersionStore), typeof(VersionStore), typeof(VersionNode), typeof(HistoryDocuments), typeof(HistoryCollections),
    typeof(VersionIndexRoot), typeof(Reconstruction), typeof(ReconstructionReport), typeof(Operation), typeof(SchemaNode),
    typeof(RelationNode), typeof(BasePage), typeof(PageManager), typeof(RootPage), typeof(DataPage), typeof(OverflowPage),
    typeof(Pages.Managers.SystemDocumentStore)
  ];

  private static readonly string[] ForbiddenNamespaces = [
    "TokkDb.Pages.Indexes", "TokkDb.Pages.Managers", "TokkDb.Buffer", "TokkDb.Pages.FreeSpace"
  ];

  private static readonly HashSet<string> ForbiddenConnectionMembers = [
    "get_Versions", "PrimaryIndex", "SecondaryIndex", "get_SystemDocuments"
  ];

  [Fact]
  public void TheGuaranteeTestsUseOnlyThePublicSurface() {
    var offenders = new List<string>();
    foreach (var (owner, method) in IlReferences.Methods(typeof(VersioningGuaranteeTests).Assembly)) {
      if (owner != typeof(VersioningGuaranteeTests) || method.Name == nameof(TheGuaranteeTestsUseOnlyThePublicSurface)) {
        continue;
      }
      foreach (var member in IlReferences.ReferencedMembers(method)) {
        var declaring = member.DeclaringType;
        if (declaring is null) {
          continue;
        }
        var outermost = declaring;
        while (outermost.DeclaringType is not null) {
          outermost = outermost.DeclaringType;
        }
        if (typeof(Exception).IsAssignableFrom(outermost)) {
          continue;
        }
        var forbidden = Forbidden.Contains(outermost)
          || Forbidden.Any(type => type.IsAssignableFrom(outermost))
          || ForbiddenNamespaces.Any(ns => outermost.Namespace == ns)
          || (outermost == typeof(TokkDbConnection) && ForbiddenConnectionMembers.Contains(member.Name))
          || (outermost.Namespace == "TokkDb.Pages.Records" && outermost != typeof(RecordIdentity));
        if (forbidden) {
          offenders.Add($"{method.Name} uses {declaring.Name}.{member.Name}");
        }
      }
    }
    Assert.Empty(offenders);
  }
}

file static class DocumentValueExtensions {
  public static Dictionary<string, IDocumentValue> AsObject(this IDocumentValue value) {
    return ((ObjectDocumentValue)value).Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
  }
}
