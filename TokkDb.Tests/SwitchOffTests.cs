using System.Text;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RP-6 and V-14: turning versioning off drops the history in one transaction, a failure in
//the middle leaves policy and history as they were, what only the history held is in neither
//file afterwards, and turning it on again gives a record a Baseline at its next change.
public class SwitchOffTests {
  private const string Notes = "Notes";

  private static Dictionary<string, IDocumentValue> Note(string title) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Body"] = new StringDocumentValue(new string('b', 200))
    };
  }

  private static List<Ulid> Populate(TokkDbConnection db) {
    db.CreateCollection(Notes, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)]);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var ids = new List<Ulid>();
    for (var i = 0; i < 6; i++) {
      var id = notes.Insert(Note($"PAST-{i}-first"));
      notes.Update(id, Note($"PAST-{i}-second"));
      notes.Update(id, Note($"LIVE-{i}"));
      ids.Add(id);
    }
    return ids;
  }

  private static bool Contains(string path, string needle) {
    return File.Exists(path) && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
  }

  [Fact]
  public void SwitchingOffDropsTheHistoryAndLeavesWhatOnlyItHeldInNeitherFile() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    using var db = new TokkDbConnection(disk);
    db.Load();
    var ids = Populate(db);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var historyName = HistoryCollections.NameFor(db.Collection(Notes).Id);
    Assert.True(Contains(file.Path, "PAST-3-second"));

    db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);

    Assert.Equal(RetentionPolicy.None, db.Collection(Notes).RetentionPolicy);
    Assert.DoesNotContain(db.Collections, collection => collection.Name == historyName);
    Assert.Equal(0, disk.Journal.Length);
    for (var i = 0; i < ids.Count; i++) {
      Assert.Equal($"LIVE-{i}", ((StringDocumentValue)notes.GetById(ids[i]).Value["Title"]).Value);
      Assert.True(notes.History(ids[i]).IsEmpty);
      foreach (var past in new[] { $"PAST-{i}-first", $"PAST-{i}-second" }) {
        Assert.False(Contains(file.Path, past), $"'{past}' is still in the database file");
        Assert.False(Contains(Journal.GetJournalPath(file.Path), past), $"'{past}' is still in the journal");
      }
    }
  }

  [Fact]
  public void AFailureInTheMiddleOfSwitchingOffLeavesPolicyAndHistoryAsTheyWere() {
    using var file = new TempDatabaseFile();
    List<Ulid> ids;
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      ids = Populate(db);
    }
    var pristine = File.ReadAllBytes(file.Path);
    var expected = Snapshot(file, ids);

    var writes = CountWritesInASwitchOff(file);
    Assert.True(writes > 5, $"only {writes} writes to aim at");
    var samples = Enumerable.Range(1, writes).Where(write => write % 4 == 1 || write == writes - 1).ToList();
    foreach (var killAfter in samples) {
      Restore(file, pristine);
      var interrupted = new FaultInjectingDiskManager(file.Path, killAfter);
      var db = new TokkDbConnection(interrupted);
      try {
        db.Load();
        db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);
      } catch (SimulatedProcessKillException) {
        //A killed process gets no further.
      } finally {
        db.Dispose();
      }
      Assert.Equal(expected, Snapshot(file, ids));
    }
  }

  private static string Snapshot(TempDatabaseFile file, List<Ulid> ids) {
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    Assert.Equal(RetentionPolicy.KeepVersions, db.Collection(Notes).RetentionPolicy);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var lines = new List<string>();
    foreach (var id in ids) {
      foreach (var version in notes.History(id).Versions) {
        lines.Add($"{id}:{version.VersionId}:{version.Kind}:{((StringDocumentValue)notes.GetAsOf(id, version.VersionId).Value["Title"]).Value}");
      }
    }
    var verification = db.Versions.Verify(Notes);
    Assert.True(verification.IsSound, verification.ToString());
    lines.Add(verification.ToString());
    return string.Join("\n", lines);
  }

  private static int CountWritesInASwitchOff(TempDatabaseFile file) {
    var snapshot = File.ReadAllBytes(file.Path);
    var disk = new FaultInjectingDiskManager(file.Path);
    using (var db = new TokkDbConnection(disk)) {
      db.Load();
      db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);
    }
    Restore(file, snapshot);
    return disk.WriteCount;
  }

  private static void Restore(TempDatabaseFile file, byte[] contents) {
    File.WriteAllBytes(file.Path, contents);
    var journal = Journal.GetJournalPath(file.Path);
    if (File.Exists(journal)) {
      File.WriteAllBytes(journal, []);
    }
  }

  [Fact]
  public void SwitchingBackOnGivesARecordABaselineAtItsNextChange() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var ids = Populate(db);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);
    db.SetRetentionPolicy(Notes, RetentionPolicy.KeepVersions);
    Assert.True(notes.History(ids[0]).IsEmpty);

    notes.Update(ids[0], Note("after-switch-on"));

    var history = notes.History(ids[0]);
    Assert.Equal([VersionKind.Baseline, VersionKind.Update], history.Versions.Select(version => version.Kind));
    Assert.Equal("LIVE-0", ((StringDocumentValue)notes.GetAsOf(ids[0], history.Versions[0].VersionId).Value["Title"]).Value);
    Assert.Equal("after-switch-on", ((StringDocumentValue)notes.GetAsOf(ids[0], history.Versions[1].VersionId).Value["Title"]).Value);
    Assert.True(notes.History(ids[1]).IsEmpty);
    Assert.True(db.VerifyHistory(Notes).IsSound);
  }
}
