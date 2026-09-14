using System.Text;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RP-5, V-17 and G-8: an erase is total within the boundary — the database file and its
//journal — as soon as it commits, and every other commit keeps its frame as before.
public class EraseTests {
  private const string Notes = "Notes";

  private static Dictionary<string, IDocumentValue> Note(string title, string body) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Body"] = new StringDocumentValue(body)
    };
  }

  private static TokkDbConnection Open(DiskManager disk) {
    var db = new TokkDbConnection(disk);
    db.Load();
    if (!db.Collections.Any(collection => collection.Name == Notes)) {
      db.CreateCollection(Notes, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)]);
      db.CreateIndex(Notes, "Title");
    }
    return db;
  }

  private static bool Contains(string path, string needle) {
    return File.Exists(path) && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
  }

  private static void AssertInNeitherFile(TempDatabaseFile file, IEnumerable<string> values) {
    var journal = Journal.GetJournalPath(file.Path);
    foreach (var value in values) {
      Assert.False(Contains(file.Path, value), $"'{value}' is still in the database file");
      Assert.False(Contains(journal, value), $"'{value}' is still in the journal");
    }
  }

  //A distinctive string in a live field, in three past versions and in a secondary index key.
  [Fact]
  public void AnErasedRecordLeavesNoByteInEitherFileAndIsUnknownToHistory() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    using var db = Open(disk);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var values = new List<string>();
    var id = notes.Insert(Note("ERASED-title-0", "ERASED-body-0-" + new string('a', 300)));
    values.AddRange(["ERASED-title-0", "ERASED-body-0"]);
    var versions = new List<Ulid> { notes.HeadVersion(id) };
    for (var i = 1; i <= 3; i++) {
      notes.Update(id, Note($"ERASED-title-{i}", $"ERASED-body-{i}-" + new string((char)('a' + i), 300)));
      values.AddRange([$"ERASED-title-{i}", $"ERASED-body-{i}"]);
      versions.Add(notes.HeadVersion(id));
    }
    var other = notes.Insert(Note("STAYS-title", "STAYS-body"));
    var operationsBefore = db.SystemDocuments.ReadAll(HistoryCollections.NameFor(db.Collection(Notes).Id))
      .Count(document => HistoryDocuments.TypeOf(document.Document) == HistoryDocuments.OperationType);
    Assert.True(Contains(file.Path, "ERASED-title-3"));

    notes.Erase(id);

    //Right after the commit, the connection still open and no further transaction.
    AssertInNeitherFile(file, values);
    Assert.Equal(0, disk.Journal.Length);
    Assert.True(Contains(file.Path, "STAYS-title"));
    Assert.Null(notes.GetById(id));
    Assert.Equal(AsOfOutcome.NoSuchRecord, notes.GetAsOf(id, DateTimeOffset.UtcNow).Outcome);
    Assert.Equal(AsOfOutcome.NoSuchRecord, notes.GetAsOf(id, versions[1].Time).Outcome);
    Assert.Throws<VersionNotFoundException>(() => notes.GetAsOf(id, versions[2]));
    Assert.True(notes.History(id).IsEmpty);
    Assert.Empty(notes.GetBy("Title", "ERASED-title-3"));
    Assert.Single(notes.GetBy("Title", "STAYS-title"));
    Assert.Equal(other, Assert.Single(notes.GetAllRecords()).RecordId);
    //The operation documents stay, for the collection's next purge (RP-5).
    Assert.Equal(operationsBefore, db.SystemDocuments.ReadAll(HistoryCollections.NameFor(db.Collection(Notes).Id))
      .Count(document => HistoryDocuments.TypeOf(document.Document) == HistoryDocuments.OperationType));
    Assert.True(db.Versions.Verify(Notes).IsSound, db.Versions.Verify(Notes).ToString());
    Assert.Equal(1u, db.Collection(Notes).RecordCount);
  }

  [Fact]
  public void AnEraseInsideALargerUnitOfWorkDiscardsThatUnitsFrameAtItsCommit() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    using var db = Open(disk);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var id = notes.Insert(Note("ERASED-inside", "ERASED-inside-body"));
    Ulid added = default;

    db.InTransaction(() => {
      added = notes.Insert(Note("ADDED-beside", "ADDED-beside-body"));
      notes.Erase(id);
      notes.Update(added, Note("ADDED-beside", "ADDED-beside-body-2"));
    });

    Assert.Equal(0, disk.Journal.Length);
    AssertInNeitherFile(file, ["ERASED-inside"]);
    Assert.True(Contains(file.Path, "ADDED-beside-body-2"));
    Assert.Equal("ADDED-beside-body-2", ((StringDocumentValue)notes.GetById(added).Value["Body"]).Value);
    Assert.True(db.Versions.Verify(Notes).IsSound);
  }

  //An ordinary commit keeps its frame as it always has, and recovery treats it as before.
  [Fact]
  public void AnOrdinaryCommitStillLeavesItsFrameAndRecoveryBehavesAsBefore() {
    using var file = new TempDatabaseFile();
    ulong committed;
    using (var disk = new DiskManager(file.Path)) {
      using var db = Open(disk);
      var notes = db.Entities(new FieldMapSerializer(), Notes);
      notes.Insert(Note("kept-frame", "kept-frame-body"));
      var frame = disk.Journal.Read();
      Assert.NotNull(frame);
      Assert.True(frame.IsCommitted);
      Assert.True(frame.Pages.Count > 0);
      committed = frame.TransactionId;
      Assert.True(disk.Journal.Length > 0);
    }
    using var reopened = new TokkDbConnection(file.Path);
    Assert.Equal(RecoveryOutcome.CommittedTransactionKept, reopened.RecoveryDecision.Outcome);
    Assert.Equal(committed, reopened.RecoveryDecision.TransactionId);
    reopened.Load();
    Assert.Single(reopened.Entities(new FieldMapSerializer(), Notes).GetAll());
  }

  [Fact]
  public void ErasingADeletedRecordRemovesItsTombstoneAndErasingAnUnknownOneIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = Open(new DiskManager(file.Path));
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var id = notes.Insert(Note("ERASED-deleted", "ERASED-deleted-body"));
    notes.Update(id, Note("ERASED-deleted-2", "ERASED-deleted-body-2"));
    notes.Delete(id);
    Assert.False(notes.History(id).IsEmpty);

    notes.Erase(id);

    Assert.True(notes.History(id).IsEmpty);
    Assert.Equal(AsOfOutcome.NoSuchRecord, notes.GetAsOf(id, DateTimeOffset.UtcNow).Outcome);
    AssertInNeitherFile(file, ["ERASED-deleted"]);
    Assert.Throws<RecordNotFoundException>(() => notes.Erase(id));
    Assert.Throws<RecordNotFoundException>(() => notes.Erase(Ulid.NewUlid()));
    Assert.True(db.Versions.Verify(Notes).IsSound);
  }

  //Under None there is no history to erase; the live image and its entries still go.
  [Fact]
  public void AnEraseUnderNoneRemovesTheLiveImage() {
    using var file = new TempDatabaseFile();
    var disk = new DiskManager(file.Path);
    using var db = Open(disk);
    db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);
    var notes = db.Entities(new FieldMapSerializer(), Notes);
    var id = notes.Insert(Note("ERASED-none", "ERASED-none-body"));

    notes.Erase(id);

    Assert.Null(notes.GetById(id));
    Assert.Empty(notes.GetBy("Title", "ERASED-none"));
    AssertInNeitherFile(file, ["ERASED-none"]);
    Assert.Equal(0, disk.Journal.Length);
  }
}
