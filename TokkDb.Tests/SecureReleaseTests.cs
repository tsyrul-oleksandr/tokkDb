using System.Text;
using TokkDb.Buffer;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RP-4 and V-17: every byte the page layer releases is cleared as it is released, whatever
//the retention policy. The database file is searched for the retired values; the journal's
//half of the boundary is step 7.3's.
public class SecureReleaseTests {
  private const string Notes = "Notes";

  private static Dictionary<string, IDocumentValue> Note(string title, string body) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Body"] = new StringDocumentValue(body)
    };
  }

  private static void CreateNotes(TokkDbConnection db, RetentionPolicy policy) {
    db.CreateCollection(Notes, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)]);
    db.CreateIndex(Notes, "Title");
    if (policy == RetentionPolicy.None) {
      db.SetRetentionPolicy(Notes, RetentionPolicy.None, dropHistory: true);
    }
  }

  private static bool Contains(byte[] haystack, string needle) {
    return haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
  }

  private static void AssertNoneRemain(byte[] file, IEnumerable<string> retired) {
    foreach (var value in retired) {
      Assert.False(Contains(file, value), $"'{value}' is still in the database file");
    }
  }

  [Fact]
  public void NothingRetiredUnderNoneRemainsInTheFile() {
    using var file = new TempDatabaseFile();
    var retired = new List<string>();
    var live = new List<string>();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      CreateNotes(db, RetentionPolicy.None);
      var notes = db.Entities(new FieldMapSerializer(), Notes);
      var ids = new List<Ulid>();
      for (var i = 0; i < 60; i++) {
        var (title, body) = ($"RETIRED-T0-{i:D3}", $"RETIRED-B0-{i:D3}-" + new string('x', 150));
        ids.Add(notes.Insert(Note(title, body)));
        retired.Add(title);
        retired.Add(body);
      }
      //Two rounds of updates retire the first two images of every record.
      for (var round = 1; round <= 2; round++) {
        for (var i = 0; i < ids.Count; i++) {
          var (title, body) = ($"RETIRED-T{round}-{i:D3}", $"RETIRED-B{round}-{i:D3}-" + new string('y', 150 + round * 40));
          notes.Update(ids[i], Note(title, body));
          retired.Add(title);
          retired.Add(body);
        }
      }
      //The last image stays live for half of them and is retired by a delete for the rest.
      for (var i = 0; i < ids.Count; i++) {
        var (title, body) = ($"{(i % 2 == 0 ? "LIVE" : "RETIRED")}-T3-{i:D3}", $"{(i % 2 == 0 ? "LIVE" : "RETIRED")}-B3-{i:D3}-" + new string('z', 120));
        notes.Update(ids[i], Note(title, body));
        (i % 2 == 0 ? live : retired).Add(title);
        (i % 2 == 0 ? live : retired).Add(body);
        if (i % 2 == 1) {
          notes.Delete(ids[i]);
        }
      }
      //More inserts than the freed slots hold contiguously: the pages compact (ST-4) to take them.
      for (var i = 0; i < 60; i++) {
        var title = $"LIVE-T4-{i:D3}";
        notes.Insert(Note(title, "LIVE-B4-" + new string('w', 230)));
        live.Add(title);
      }
      Assert.Equal(90, notes.GetAll().Count());
    }
    var bytes = File.ReadAllBytes(file.Path);
    AssertNoneRemain(bytes, retired);
    Assert.All(live, value => Assert.True(Contains(bytes, value), $"'{value}' should be in the file"));
  }

  //A record too large for a page takes an overflow chain (ST-5); freeing the chain clears it.
  [Fact]
  public void AFreedOverflowChainIsCleared() {
    using var file = new TempDatabaseFile();
    var body = "RETIRED-OVERFLOW-" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"chunk{i:D5}|"));
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      CreateNotes(db, RetentionPolicy.None);
      var notes = db.Entities(new FieldMapSerializer(), Notes);
      var id = notes.Insert(Note("big", body));
      Assert.True(Contains(File.ReadAllBytes(file.Path), "chunk01999|"));
      notes.Delete(id);
    }
    var bytes = File.ReadAllBytes(file.Path);
    Assert.False(Contains(bytes, "RETIRED-OVERFLOW"));
    Assert.False(Contains(bytes, "chunk01999|"));
    Assert.False(Contains(bytes, "chunk00000|"));
  }

  //A system document rewritten in place with a shorter one: the rest of its slot is cleared.
  [Fact]
  public void TheTailOfASlotRewrittenInPlaceIsCleared() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      CreateNotes(db, RetentionPolicy.None);
      db.SetMetadata(Notes, new Dictionary<string, string> { ["note"] = "RETIRED-METADATA-" + new string('m', 400) });
      db.SetMetadata(Notes, new Dictionary<string, string> { ["note"] = "short" });
      Assert.Equal("short", db.Metadata(Notes)["note"]);
    }
    var bytes = File.ReadAllBytes(file.Path);
    Assert.False(Contains(bytes, "RETIRED-METADATA"));
    //And the slot itself: every byte beyond the record it now holds is zero.
    var slots = 0;
    foreach (var slot in Slots(file, SystemCollections.Settings)) {
      var record = StoredRecordUtilities.FromBuffer(slot);
      var length = StoredRecordUtilities.GetBytesLength(record.Header, record.Document);
      Assert.True(length < slot.Length, "the rewritten document should be shorter than its slot");
      for (var i = length; i < slot.Length; i++) {
        Assert.True(slot.ReadByte(i) == 0, $"byte {i} of a {slot.Length} byte slot holding {length} bytes is not cleared");
      }
      slots++;
    }
    Assert.True(slots > 0);
  }

  //After DropIndex, every page the index occupied reads back cleared apart from its header.
  [Fact]
  public void ADroppedIndexLeavesItsPagesCleared() {
    using var file = new TempDatabaseFile();
    List<uint> pages;
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      CreateNotes(db, RetentionPolicy.None);
      var notes = db.Entities(new FieldMapSerializer(), Notes);
      for (var i = 0; i < 400; i++) {
        notes.Insert(Note($"KEY-{Guid.NewGuid():N}-{i:D4}", "b"));
      }
      pages = db.SecondaryIndex(Notes, "Title").Nodes().Select(node => node.Index).ToList();
      Assert.True(pages.Count > 3, $"only {pages.Count} index pages");
      Assert.True(db.DropIndex(Notes, "Title"));
      Assert.Null(db.SecondaryIndex(Notes, "Title"));
    }
    var bytes = File.ReadAllBytes(file.Path);
    Assert.False(Contains(bytes, "KEY-") is false, "the live records still hold their titles");
    var pageSize = ReadPageSize(file);
    foreach (var page in pages) {
      var content = bytes.AsSpan((int)(page * pageSize) + BasePage.StartContentBufferPosition,
        pageSize - BasePage.StartContentBufferPosition - BasePage.ControlAreaByteSize);
      Assert.True(content.IndexOfAnyExcept((byte)0) < 0, $"page {page} of the dropped index is not cleared");
    }
  }

  //After DropCollection on a versioned collection with a secondary index and some history,
  //neither the database file nor its journal holds any of its values, current or historical
  //(the journal half is V-17's rule, step 7.3).
  [Fact]
  public void ADroppedVersionedCollectionLeavesNoValueInEitherFile() {
    using var file = new TempDatabaseFile();
    var values = new List<string>();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      CreateNotes(db, RetentionPolicy.KeepVersions);
      db.CreateCollection("Other", [new ColumnDescriptor("Title", ValueTypeEnum.String)]);
      var notes = db.Entities(new FieldMapSerializer(), Notes);
      var ids = new List<Ulid>();
      for (var i = 0; i < 30; i++) {
        var (title, body) = ($"GONE-T0-{i:D3}", $"GONE-B0-{i:D3}-" + new string('x', 200));
        ids.Add(notes.Insert(Note(title, body)));
        values.AddRange([title, body]);
      }
      for (var i = 0; i < ids.Count; i++) {
        var (title, body) = ($"GONE-T1-{i:D3}", $"GONE-B1-{i:D3}-" + new string('y', 200));
        notes.Update(ids[i], Note(title, body));
        values.AddRange([title, body]);
        if (i % 3 == 0) {
          notes.Delete(ids[i]);
        }
      }
      Assert.True(db.Versions.Verify(Notes).IsSound);
      var other = db.Entities(new FieldMapSerializer(), "Other");
      other.Insert(new Dictionary<string, IDocumentValue> { ["Title"] = new StringDocumentValue("STAYS-other") });

      Assert.True(db.DropCollection(Notes));

      AssertNoneRemain(File.ReadAllBytes(Journal.GetJournalPath(file.Path)), values);
      Assert.Equal("STAYS-other", ((StringDocumentValue)other.GetAll().Single()["Title"]).Value);
    }
    var bytes = File.ReadAllBytes(file.Path);
    AssertNoneRemain(bytes, values);
    Assert.True(Contains(bytes, "STAYS-other"));
    var journal = Journal.GetJournalPath(file.Path);
    Assert.True(!File.Exists(journal) || new FileInfo(journal).Length == 0, "the drop's journal frame was not discarded");
  }

  private static ushort ReadPageSize(TempDatabaseFile file) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    return RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize;
  }

  //Every live slot of a collection, read straight off the file.
  private static IEnumerable<BufferSlice> Slots(TempDatabaseFile file, string collectionName) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var rootPage = pageManager.LoadPage<RootPage>(Configuration.TokkConstants.RootPageIndex);
    var next = FindDataFirstPage(pageManager, rootPage.CollectionsFirstPageId, collectionName);
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      foreach (var item in page.GetItems()) {
        yield return item;
      }
      next = page.NextPageIndex;
    }
  }

  private static uint FindDataFirstPage(PageManager pageManager, uint cataloguePage, string collectionName) {
    var next = cataloguePage;
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      foreach (var item in page.GetItems()) {
        var descriptor = CollectionDescriptorDocument.Read(StoredRecordUtilities.FromBuffer(item).Document);
        if (descriptor.Name == collectionName) {
          return descriptor.DataFirstPage;
        }
      }
      next = page.NextPageIndex;
    }
    return default;
  }
}
