using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//§7: the scenarios that define "done" which no other test states as a whole. S-1 is
//RestoreDeletedTests, S-4 VersionDeleteTests, S-6 PurgeTests, S-7, S-8 and S-10 the assistant's
//UndoGuaranteeTests, S-9 the related restore's tests; the N scenarios are named where they live
//in §11's table.
public class ScenarioTests {
  private const string Docs = "Doc";

  private static Dictionary<string, IDocumentValue> Doc(string title, int year, string note = "keep") {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Year"] = new IntDocumentValue(year),
      ["Note"] = new StringDocumentValue(note), ["Body"] = new StringDocumentValue(new string('b', 300))
    };
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, int k = 8, double ratio = 0.5) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Docs, [
      new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)
    ]);
    db.SetRetentionPolicy(Docs, RetentionPolicy.KeepVersions, k, ratio);
    return db;
  }

  //S-2 — A branch: four versions, the second restored and edited; two leaves, both readable,
  //the diff between them correct, and nothing removed.
  [Fact]
  public void S2ABranch() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Docs);
    var id = docs.Insert(Doc("v1", 1));
    var versions = new List<Ulid> { docs.HeadVersion(id) };
    foreach (var i in new[] { 2, 3, 4 }) {
      docs.Update(id, Doc($"v{i}", i));
      versions.Add(docs.HeadVersion(id));
    }
    docs.Restore(id, versions[1]);
    docs.Update(id, Doc("v2-edited", 22));
    var edited = docs.HeadVersion(id);

    var history = docs.History(id);
    Assert.Equal([versions[3], edited], history.Leaves.Order());
    Assert.Equal(6, history.Versions.Count);
    Assert.Equal("v4", ((StringDocumentValue)docs.GetAsOf(id, versions[3]).Value["Title"]).Value);
    Assert.Equal("v2-edited", ((StringDocumentValue)docs.GetAsOf(id, edited).Value["Title"]).Value);
    //The diff between the leaves: what turns the one into the other.
    var diff = docs.Diff(id, versions[3], edited);
    var expected = DocumentDiff.Compute(new FieldMapSerializer().Create(Doc("v4", 4), id).Value,
      new FieldMapSerializer().Create(Doc("v2-edited", 22), id).Value);
    Assert.True(CanonicalValue.Equal(expected.ToDocumentValue(), diff.Delta.ToDocumentValue()));
    Assert.True(CanonicalValue.Equal(new FieldMapSerializer().Create(Doc("v2-edited", 22), id).Value,
      diff.Delta.ApplyTo(new FieldMapSerializer().Create(Doc("v4", 4), id).Value)));
    Assert.True(db.VerifyHistory(Docs).IsSound);
  }

  //S-3 — History across schema changes: updates interleaved with adding a column, a rename, a
  //lossless retype, a lossy retype and a removal, then Rewrite. Every version reads through the
  //current schema, with the lossy and removed values reported as unmapped, and as stored; and
  //SchemaAsOf answers for every moment in between.
  [Fact]
  public void S3HistoryAcrossSchemaChanges() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Docs);
    var id = docs.Insert(Doc("one", 2001, "text"));
    var stored = new Dictionary<Ulid, Dictionary<string, IDocumentValue>> { [docs.HeadVersion(id)] = Doc("one", 2001, "text") };
    var moments = new List<(DateTimeOffset Moment, string[] Columns)>();
    string[] Columns() => db.Collection(Docs).Columns.Select(column => column.Name).ToArray();
    void Change(IEnumerable<ColumnDescriptor> columns, params ColumnMigration[] steps) {
      db.SetColumns(Docs, columns.ToList(), steps.Length == 0 ? null : steps);
      Thread.Sleep(2);
      moments.Add((DateTimeOffset.UtcNow, Columns()));
      Thread.Sleep(2);
    }
    Thread.Sleep(2);
    moments.Add((DateTimeOffset.UtcNow, Columns()));

    //Add a column.
    Change([new("Title", ValueTypeEnum.String), new("Year", ValueTypeEnum.Int), new("Note", ValueTypeEnum.String),
      new("Body", ValueTypeEnum.String), new("Extra", ValueTypeEnum.String)]);
    var withExtra = Doc("two", 2002, "text");
    withExtra["Extra"] = new StringDocumentValue("e");
    docs.Update(id, withExtra);
    stored[docs.HeadVersion(id)] = withExtra;
    //A rename.
    Change([new("Title", ValueTypeEnum.String), new("Published", ValueTypeEnum.Int), new("Note", ValueTypeEnum.String),
      new("Body", ValueTypeEnum.String), new("Extra", ValueTypeEnum.String)], ColumnMigration.Rename(db.Collection(Docs).SchemaVersion, "Year", "Published"));
    var renamed = new Dictionary<string, IDocumentValue>(withExtra, StringComparer.Ordinal);
    renamed.Remove("Year");
    renamed["Published"] = new IntDocumentValue(2003);
    renamed["Title"] = new StringDocumentValue("three");
    docs.Update(id, renamed);
    stored[docs.HeadVersion(id)] = renamed;
    //A lossless retype: numbers become text.
    Change([new("Title", ValueTypeEnum.String), new("Published", ValueTypeEnum.String), new("Note", ValueTypeEnum.String),
      new("Body", ValueTypeEnum.String), new("Extra", ValueTypeEnum.String)], ColumnMigration.Retype(db.Collection(Docs).SchemaVersion, "Published", ValueTypeEnum.String));
    //A lossy retype: text becomes numbers, which "text" cannot.
    Change([new("Title", ValueTypeEnum.String), new("Published", ValueTypeEnum.String), new("Note", ValueTypeEnum.Int),
      new("Body", ValueTypeEnum.String), new("Extra", ValueTypeEnum.String)], ColumnMigration.Retype(db.Collection(Docs).SchemaVersion, "Note", ValueTypeEnum.Int));
    var retyped = new Dictionary<string, IDocumentValue>(renamed, StringComparer.Ordinal);
    retyped["Published"] = new StringDocumentValue("2004");
    retyped["Note"] = new IntDocumentValue(4);
    retyped["Title"] = new StringDocumentValue("four");
    docs.Update(id, retyped);
    stored[docs.HeadVersion(id)] = retyped;
    //A removal.
    Change([new("Title", ValueTypeEnum.String), new("Published", ValueTypeEnum.String), new("Note", ValueTypeEnum.Int),
      new("Body", ValueTypeEnum.String)], ColumnMigration.Remove(db.Collection(Docs).SchemaVersion, "Extra"));
    Assert.True(db.Rewrite(Docs) > 0);

    foreach (var (version, document) in stored) {
      var read = docs.GetAsOf(id, version);
      var asStored = docs.GetStoredAsOf(id, version);
      //As stored: the document as written, or — for the head, which Rewrite migrated — the
      //same document at the current schema; every original value is still there through one or the other.
      var storedFields = ((ObjectDocumentValue)asStored.Document.Value).Values;
      Assert.Equal(storedFields.Count, read.Value.Count + read.Unmapped.Count);
      foreach (var unmapped in read.Unmapped) {
        Assert.True(CanonicalValue.Equal(storedFields[unmapped.Column], unmapped.Original));
      }
      //Through the current schema: Year reads as Published text, the "text" note is reported
      //lossy, and Extra is reported removed where it existed.
      Assert.Equal(((IntDocumentValue)document.GetValueOrDefault("Year") ?? (IDocumentValue)document.GetValueOrDefault("Published")) switch {
        IntDocumentValue year => year.Value.ToString(),
        StringDocumentValue text => text.Value,
        _ => null
      }, ((StringDocumentValue)read.Value["Published"]).Value);
      if (document["Note"] is StringDocumentValue) {
        Assert.Contains(read.Unmapped, item => item.Column == "Note" && item.Reason == UnmappedReason.RetypeLossy);
      } else {
        Assert.Equal(4, ((IntDocumentValue)read.Value["Note"]).Value);
      }
      if (document.ContainsKey("Extra")) {
        Assert.Contains(read.Unmapped, item => item.Column == "Extra" && item.Reason == UnmappedReason.ColumnRemoved);
      }
    }
    //SchemaAsOf for every moment in between: the columns as they were declared then.
    foreach (var (moment, columns) in moments) {
      var snapshot = db.SchemaAsOf(Docs, moment);
      Assert.False(snapshot.BeforeRecordedHistory);
      Assert.Equal(columns, snapshot.Schema.Columns.Select(column => column.Name));
    }
    Assert.True(db.VerifyHistory(Docs).IsSound, db.VerifyHistory(Docs).ToString());
  }

  //S-5 — The bound: with k = 8 and 100 updates, some of them complete rewrites, no
  //reconstruction applies more than seven deltas.
  [Fact]
  public void S5TheBound() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, k: 8);
    var docs = db.Entities(new FieldMapSerializer(), Docs);
    var id = docs.Insert(Doc("v0", 0));
    for (var i = 1; i <= 100; i++) {
      var document = i % 9 == 0
        ? new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
          ["Title"] = new StringDocumentValue($"rewritten {i}"), ["Year"] = new IntDocumentValue(3000 + i),
          ["Note"] = new StringDocumentValue($"all new {i}"), ["Body"] = new StringDocumentValue(new string((char)('c' + i % 20), 300))
        }
        : Doc($"v{i}", i);
      docs.Update(id, document);
    }
    var worst = 0;
    foreach (var version in docs.History(id).Versions) {
      var report = docs.GetStoredAsOf(id, version.VersionId).Report;
      worst = Math.Max(worst, report.DeltasApplied);
    }
    Assert.Equal(101, docs.History(id).Versions.Count);
    Assert.True(worst <= 7, $"a reconstruction applied {worst} deltas");
    Assert.True(worst >= 5, $"the worst reconstruction applied only {worst} deltas: the bound was not exercised");
  }
}
