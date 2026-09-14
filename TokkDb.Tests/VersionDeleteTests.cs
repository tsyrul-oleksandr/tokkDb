using TokkDb.Disk;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Tests.Fixtures;
using Xunit;

namespace TokkDb.Tests;

//WV-3 and WV-4: a delete leaves a tombstone, a head without a node gets its Baseline, and
//turning versioning on changes nothing already stored (G-9, S-4).
public class VersionDeleteTests {
  private const string Collection = nameof(Person);

  private static TokkDbConnection NewVersioned(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.CreateIndex(Collection, "Age");
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions);
    return db;
  }

  [Fact]
  public void ADeletedRecordIsGoneEverywhereAndItsHistoryEndsInATombstone() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    var ids = Enumerable.Range(0, 5).Select(i => people.Insert(TestPeople.Numbered(i))).ToList();
    var doomed = ids[2];
    people.Update(doomed, new Person { Id = 2, Name = "Person-2", Age = 99, Passport = new Passport("X"), Tags = [] });
    var head = people.HeadVersion(doomed);

    people.Delete(doomed);

    Assert.Null(people.GetById(doomed));
    Assert.DoesNotContain(people.GetAll(), person => person.Id == 2);
    Assert.Empty(people.GetBy("Age", 99));
    var byId = new Documents.Path.Expressions.ComparisonExpression(
      new Documents.Path.Expressions.PropertyExpression("Id") { Parent = new Documents.Path.Expressions.RootExpression() },
      Documents.Path.Expressions.ComparisonOperator.Equal,
      new Documents.Path.Expressions.ConstantExpression(new IntDocumentValue(2)), Values.ValueTypeEnum.Int);
    Assert.Empty(people.Query().Where(byId).Run().Records);
    Assert.Equal(4u, db.Collection(Collection).RecordCount);
    Assert.Null(db.PrimaryIndex(Collection).Find(Documents.Keys.KeyEncoder.Encode(doomed).Bytes));

    var nodes = db.Versions.Nodes(Collection, doomed);
    Assert.Equal(3, nodes.Count);
    Assert.Equal(VersionKind.Insert, nodes[0].Kind);
    Assert.NotNull(nodes[0].Image);
    Assert.Equal(VersionKind.Update, nodes[1].Kind);
    Assert.Equal(head, nodes[1].VersionId);
    var tombstone = nodes[2];
    Assert.Equal(VersionKind.Delete, tombstone.Kind);
    Assert.Equal(head, tombstone.Parent);
    Assert.Null(tombstone.Delta);
    Assert.Null(tombstone.Image);
    Assert.Equal(nodes[1].Distance + 1, tombstone.Distance);
    Assert.True(tombstone.VersionId.CompareTo(head) > 0);
    //The head of a deleted record is its tombstone.
    Assert.Equal(tombstone.VersionId, people.HeadVersion(doomed));
    Assert.Equal(tombstone.VersionId, db.Versions.Head(Collection, doomed).VersionId);
  }

  //A keyframe head keeps its image when it is deleted: the tombstone is what the record ends
  //with, and the image is what every reconstruction of the keyframe needs.
  [Fact]
  public void DeletingAKeyframeHeadCopiesItsImageIntoItsNode() {
    using var file = new TempDatabaseFile();
    using var db = NewVersioned(file);
    var people = db.Entities<Person>();
    var id = people.Insert(TestPeople.Numbered(1));
    people.Delete(id);
    var nodes = db.Versions.Nodes(Collection, id);
    Assert.Equal(2, nodes.Count);
    Assert.NotNull(nodes[0].Image);
    Assert.Equal("Person-1", ((StringDocumentValue)nodes[0].Image.Values["Name"]).Value);
    Assert.Equal(VersionKind.Delete, nodes[1].Kind);
  }

  //Every page of the file, by index.
  private static List<byte[]> Pages(TempDatabaseFile file) {
    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    var pageSize = RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize;
    pageManager.SetPageSize(pageSize);
    var bytes = File.ReadAllBytes(file.Path);
    var pages = new List<byte[]>();
    for (var offset = 0; offset + pageSize <= bytes.Length; offset += pageSize) {
      pages.Add(bytes[offset..(offset + pageSize)]);
    }
    return pages;
  }

  //The pages that hold the record's live image, before and after: found through the primary
  //index of a read-only connection.
  private static uint PageOf(TempDatabaseFile file, string collection, Ulid recordId) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var address = reader.PrimaryIndex(collection).Find(Documents.Keys.KeyEncoder.Encode(recordId).Bytes);
    return address!.Value.PageIndex;
  }

  //WV-4's acceptance criterion and S-4, on the step 0.2 fixture: switched on and one record
  //updated, that record has Baseline and Update, the others nothing, and every page holding
  //only untouched records is byte-identical before and after.
  [Fact]
  public void OnTheFixtureSwitchingOnAndUpdatingOneRecordTouchesOnlyThatRecord() {
    using var file = PreVersioningFixture.Copy();
    var before = Pages(file);
    Ulid updated;
    List<Ulid> others;
    using (var probe = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly)) {
      probe.Load();
      var expenses = probe.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses).GetAllRecords().ToList();
      updated = expenses.Single(record => record.Value.Id == 7).RecordId;
      others = expenses.Where(record => record.Value.Id != 7).Select(record => record.RecordId).ToList();
    }
    var pageBefore = PageOf(file, PreVersioningFixture.Expenses, updated);
    var dataPagesBefore = new HashSet<uint>();
    using (var probe = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly)) {
      probe.Load();
      foreach (var collection in new[] { PreVersioningFixture.Expenses, PreVersioningFixture.Conferences }) {
        foreach (var record in probe.PrimaryIndex(collection).Scan()) {
          dataPagesBefore.Add(record.Address.PageIndex);
        }
      }
    }

    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      db.SetRetentionPolicy(PreVersioningFixture.Expenses, RetentionPolicy.KeepVersions);
      var expenses = db.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses);
      var value = expenses.GetById(updated).Value;
      value.Amount += 1;
      expenses.Update(updated, value);

      var nodes = db.Versions.Nodes(PreVersioningFixture.Expenses, updated);
      Assert.Equal(2, nodes.Count);
      Assert.Equal(VersionKind.Baseline, nodes[0].Kind);
      Assert.NotNull(nodes[0].Image);
      //As stored: the fixture wrote this record before its column was renamed.
      Assert.True(nodes[0].Image.Values.ContainsKey("Note"));
      Assert.Equal(1, nodes[0].ImageSchemaVersion);
      Assert.Equal(VersionKind.Update, nodes[1].Kind);
      Assert.Equal(nodes[0].VersionId, nodes[1].Parent);
      Assert.Equal(nodes[1].VersionId, expenses.HeadVersion(updated));
      foreach (var other in others) {
        Assert.Empty(db.Versions.Nodes(PreVersioningFixture.Expenses, other));
      }
    }

    var pageAfter = PageOf(file, PreVersioningFixture.Expenses, updated);
    var after = Pages(file);
    Assert.True(after.Count >= before.Count);
    foreach (var page in dataPagesBefore.Where(page => page != pageBefore && page != pageAfter)) {
      Assert.True(before[(int)page].AsSpan().SequenceEqual(after[(int)page]), $"page {page} holding only untouched records changed");
    }
    Assert.NotEmpty(dataPagesBefore.Where(page => page != pageBefore && page != pageAfter));
  }
}
