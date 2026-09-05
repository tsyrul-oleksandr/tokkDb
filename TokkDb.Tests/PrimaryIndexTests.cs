using TokkDb.Documents.Keys;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//DC-4 and D-2. The primary index keyed by record identity, and what it changes about the
//three paths that used to walk every data page of a collection.
public class PrimaryIndexTests {
  private const string Collection = nameof(Person);

  private readonly ITestOutputHelper _output;

  public PrimaryIndexTests(ITestOutputHelper output) {
    _output = output;
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    return db;
  }

  private static byte[] Key(Ulid recordId) {
    return KeyEncoder.Encode(recordId).Bytes;
  }

  //One transaction for the whole fixture. An insert opens a transaction nested inside this
  //one and hands its pages up rather than committing, so building a fixture costs one set of
  //fsyncs instead of one per record.
  private static List<Ulid> Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Person>();
    var ids = new List<Ulid>(count);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        ids.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });
    return ids;
  }

  [Fact]
  public void EveryInsertedRecordHasAnEntryPointingAtWhereItLies() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 500);

    var tree = db.PrimaryIndex(Collection);
    Assert.Equal(500, tree.Scan().Count());
    foreach (var id in ids) {
      //D-2: a page and a slot, and the record really is in that slot.
      var address = tree.Find(Key(id));
      Assert.NotNull(address);
      Assert.NotEqual(default, address.Value.PageIndex);
      Assert.Equal(id, db.Entities<Person>().GetById(id).RecordId);
    }
  }

  //The done-when, at the scale a unit test can afford. The count is what the benchmark
  //raises to 100 000; what is asserted here is that the cost does not follow the collection.
  [Fact]
  public void ALookupByIdReadsADescentOfPagesAndNotTheWholeCollection() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 20_000);

    var tree = db.PrimaryIndex(Collection);
    var dataPages = file.PageCount;
    var height = tree.Height();

    var readsBefore = db.PageReadCount;
    Assert.NotNull(db.Entities<Person>().GetById(ids[0]));
    var oldest = db.PageReadCount - readsBefore;

    readsBefore = db.PageReadCount;
    Assert.NotNull(db.Entities<Person>().GetById(ids[^1]));
    var newest = db.PageReadCount - readsBefore;

    _output.WriteLine($"{file.PageCount:N0} pages in the file, tree height {height}: " +
      $"{oldest} pages read for the first record, {newest} for the last");
    //A descent of the tree plus the data page the entry points at.
    Assert.InRange(oldest, 1, height + 1);
    Assert.InRange(newest, 1, height + 1);
    //And what it replaced: the scan would have read every data page of the collection.
    Assert.True(oldest * 20 < dataPages, $"{oldest} pages is not a saving over {dataPages}");
  }

  //VR-12 writes a new image somewhere else and retires the old one. The entry has to follow
  //the record, and there must still be exactly one entry for it.
  [Fact]
  public void AnUpdateRepointsTheEntryRatherThanAddingASecondOne() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 200);
    var entities = db.Entities<Person>();
    var tree = db.PrimaryIndex(Collection);

    //A same-sized image can land in the very slot the one it replaces just freed, so the
    //address staying put is allowed. One entry, addressing the live image, is the invariant.
    entities.Update(ids[100], TestPeople.Numbered(999));
    Assert.NotNull(tree.Find(Key(ids[100])));
    Assert.Equal(200, tree.Scan().Count());
    Assert.Equal(999, entities.GetById(ids[100]).Value.Id);

    //A much larger image cannot fit where the old one was, so this one really moves.
    var before = tree.Find(Key(ids[50]));
    entities.Update(ids[50], new Person {
      Id = 777, Name = new string('x', 3_000), Age = 40,
      Passport = new Passport("ST-777777"), Tags = [new Tag(new string('y', 2_000))]
    });
    var after = tree.Find(Key(ids[50]));
    Assert.NotEqual(before, after);
    Assert.Equal(200, tree.Scan().Count());
    Assert.Equal(777, entities.GetById(ids[50]).Value.Id);

    //Every other record kept the entry it had.
    Assert.All(ids.Where(id => id != ids[100] && id != ids[50]),
      id => Assert.NotNull(tree.Find(Key(id))));
  }

  [Fact]
  public void ADeleteTakesTheEntryWithTheRecord() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 200);
    var entities = db.Entities<Person>();
    var tree = db.PrimaryIndex(Collection);

    entities.Delete(ids[50]);

    Assert.Null(tree.Find(Key(ids[50])));
    Assert.Null(entities.GetById(ids[50]));
    Assert.Equal(199, tree.Scan().Count());
    Assert.Equal(199, entities.GetAll().Count());
  }

  //D-2's reason for existing. Compaction slides records down over the gaps that freed ones
  //left, so a record moves inside its page — and the entry, which names a slot and not a
  //byte offset, does not change and does not have to be found and rewritten.
  [Fact]
  public void CompactionMovesRecordsInsideAPageWithoutTouchingOneEntry() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    var ids = Fill(db, 400);
    var tree = db.PrimaryIndex(Collection);

    //Gaps everywhere, then records large enough that the gaps have to be closed before one
    //of them fits.
    foreach (var id in ids.Where((_, i) => i % 2 == 0)) {
      entities.Delete(id);
    }
    var survivors = ids.Where((_, i) => i % 2 == 1).ToList();
    var addressesBefore = survivors.ToDictionary(id => id, id => tree.Find(Key(id)));

    for (var i = 0; i < 200; i++) {
      entities.Insert(new Person {
        Id = 10_000 + i, Name = new string('x', 200), Age = 40,
        Passport = new Passport($"ST-{i:D6}"), Tags = [new Tag(new string('y', 100))]
      });
    }

    //Not one survivor's entry changed, and every one of them still reads back.
    foreach (var id in survivors) {
      Assert.Equal(addressesBefore[id], tree.Find(Key(id)));
      Assert.Equal(id, entities.GetById(id).RecordId);
    }
    Assert.Equal(400, tree.Scan().Count());
  }

  //A record too big for a page keeps its header on the page and its body in an overflow
  //chain (ST-5), so the entry addresses the prefix rather than the record. The lookup has to
  //come back with the whole thing.
  [Fact]
  public void ARecordWithAnOverflowChainIsFoundByIdAndReadBackWhole() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<LargeDocument>());
    var entities = db.Entities<LargeDocument>();
    var original = LargeDocument.OfSize(1, 200_000);

    var recordId = entities.Insert(original);
    var read = entities.GetById(recordId);
    Assert.NotNull(read);
    Assert.Equal(original.Text, read.Value.Text);
    Assert.Equal(original.Sections.Length, read.Value.Sections.Length);

    //And the entry follows the record when the chain is rewritten somewhere else.
    entities.Update(recordId, LargeDocument.OfSize(2, 300_000));
    Assert.Equal(300_000, entities.GetById(recordId).Value.Text.Length);
    Assert.Single(db.PrimaryIndex(nameof(LargeDocument)).Scan());

    entities.Delete(recordId);
    Assert.Null(entities.GetById(recordId));
    Assert.Empty(db.PrimaryIndex(nameof(LargeDocument)).Scan());
  }

  [Fact]
  public void TheIndexIsReadBackFromTheFileAndAnswersTheFirstLookupAfterAReopen() {
    using var file = new TempDatabaseFile();
    List<Ulid> ids;
    using (var db = NewDatabase(file)) {
      ids = Fill(db, 5_000);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(5_000, reopened.PrimaryIndex(Collection).Scan().Count());
    Assert.Equal(ids[2_500], reopened.Entities<Person>().GetById(ids[2_500]).RecordId);
  }

  [Fact]
  public void ARolledBackInsertLeavesNoEntryBehind() {
    using var file = new TempDatabaseFile();
    List<Ulid> ids;
    using (var db = NewDatabase(file)) {
      ids = Fill(db, 500);
      var entities = db.Entities<Person>();
      Assert.Throws<InvalidOperationException>(() => db.InTransaction(() => {
        for (var i = 0; i < 500; i++) {
          entities.Insert(TestPeople.Numbered(i));
        }
        throw new InvalidOperationException("rolled back on purpose");
      }));
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(500, reopened.PrimaryIndex(Collection).Scan().Count());
    Assert.All(ids, id => Assert.NotNull(reopened.Entities<Person>().GetById(id)));
  }

  //A file written before the index existed has records and no tree. Answering its lookups
  //out of an index holding only what has been written since would be a confident wrong
  //answer, so the tree is built once from what is already there.
  [Fact]
  public void ACollectionWithRecordsAndNoTreeIsIndexedBeforeTheNextWriteMakesItIncomplete() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>();
    var ids = Fill(db, 1_000);

    //What such a file looks like: the records are there, the catalogue points at no tree.
    db.InTransaction(() => db.Collection(Collection).PrimaryIndexRoot = default);
    db.InTransaction(() => { });
    Assert.True(db.PrimaryIndex(Collection).IsEmpty);
    //Until something writes, the lookup is the scan it used to be, and it still answers.
    Assert.Equal(ids[500], entities.GetById(ids[500]).RecordId);

    var latest = entities.Insert(TestPeople.Numbered(9_999));

    Assert.Equal(1_001, db.PrimaryIndex(Collection).Scan().Count());
    Assert.All(ids, id => Assert.NotNull(entities.GetById(id)));
    Assert.NotNull(entities.GetById(latest));
  }

  //The catalogue is what a tree reads its own root out of, so _collections cannot have one
  //until page 0 holds it. Everything about the catalogue still works without.
  [Fact]
  public void TheSystemCollectionsAreNotIndexedAndStillWork() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 100);
    db.CreateCollection("Second", []);

    Assert.All(db.Collections.Where(descriptor => descriptor.IsSystem),
      descriptor => Assert.Equal(default, descriptor.PrimaryIndexRoot));
    Assert.NotEqual(default, db.Collection(Collection).PrimaryIndexRoot);
    Assert.NotNull(db.Collection("Second"));
    Assert.Equal(100, db.Entities<Person>().GetAll().Count());
  }

  //D-1's premise, and it only holds because the identifier is minted monotonically. A Ulid
  //is time-ordered to the millisecond; inside one it is random, and a bulk load happens
  //inside a handful of them. Ulid.NewUlid() is measured here beside the other two so the
  //difference is the test's subject rather than an assumption.
  [Fact]
  public void MonotonicIdentifiersAppendAtTheEdgeWhereRawUlidsAndRandomOnesScatter() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    const int count = 100_000;
    var random = new Random(17);

    var monotonic = Build(db, Collection, count, () => RecordIdentity.Next());
    var rawUlid = Build(db, "RawUlid", count, Ulid.NewUlid);
    var scattered = Build(db, "Scattered", count, () => {
      var bytes = new byte[16];
      random.NextBytes(bytes);
      return new Ulid(bytes);
    });

    foreach (var (name, tree) in new[] {
      ("RecordIdentity.Next()", monotonic), ("Ulid.NewUlid()", rawUlid), ("random bytes", scattered)
    }) {
      _output.WriteLine($"{name,-22} {tree.LeafSplits,7:N0} leaf splits  {tree.Leaves().Count(),6:N0} leaves  " +
        $"{count / tree.Leaves().Count(),4} entries/leaf  height {tree.Height()}");
    }

    //Appending splits the rightmost leaf and nothing else, and leaves it full behind it.
    Assert.True(monotonic.LeafSplits * 3 < scattered.LeafSplits * 2,
      $"monotonic ids split {monotonic.LeafSplits} times against random's {scattered.LeafSplits}");
    Assert.True(monotonic.Leaves().Count() * 3 < scattered.Leaves().Count() * 2,
      $"monotonic ids needed {monotonic.Leaves().Count()} leaves against random's {scattered.Leaves().Count()}");
    //And the finding this test exists to pin: a raw Ulid does not do it.
    Assert.True(rawUlid.LeafSplits > monotonic.LeafSplits * 3 / 2,
      $"Ulid.NewUlid() split {rawUlid.LeafSplits} times, close to the monotonic {monotonic.LeafSplits}");
  }

  //The property the decision assumes: strictly ascending, however fast they are asked for.
  [Fact]
  public void RecordIdentitiesAscendEvenWithinOneMillisecond() {
    var identities = Enumerable.Range(0, 100_000).Select(_ => RecordIdentity.Next()).ToList();
    for (var i = 1; i < identities.Count; i++) {
      Assert.True(identities[i].CompareTo(identities[i - 1]) > 0,
        $"identity {i} does not follow the one before it");
    }
    //And the encoded keys ascend with them, which is what the tree actually compares.
    for (var i = 1; i < identities.Count; i++) {
      Assert.True(KeyComparer.Compare(Key(identities[i - 1]), Key(identities[i])) < 0);
    }
  }

  private static BPlusTree Build(TokkDbConnection db, string collectionName, int count, Func<Ulid> mint) {
    if (collectionName != Collection) {
      db.CreateCollection(collectionName, []);
    }
    var tree = db.PrimaryIndex(collectionName);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        tree.Insert(Key(mint()), new DocumentAddress((uint)(i / 50 + 1), (ushort)(i % 50)));
      }
    });
    return tree;
  }
}
