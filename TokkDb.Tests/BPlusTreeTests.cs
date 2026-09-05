using TokkDb.Documents.Keys;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//DC-4 and D-2. What is checked here is that it is a B+Tree and not a B-tree: every entry in
//a leaf, the leaves linked, the interior nodes holding keys only, and the whole thing on
//disk rather than rebuilt at open.
public class BPlusTreeTests {
  private const string Collection = nameof(Person);

  private readonly ITestOutputHelper _output;

  public BPlusTreeTests(ITestOutputHelper output) {
    _output = output;
  }

  private static byte[] Key(long value) {
    return KeyEncoder.Encode(value).Bytes;
  }

  //A stand-in for where a record would be: what it points at does not matter here, only
  //that the pointer is a page and a slot (D-2) and comes back unchanged.
  private static DocumentAddress Address(long value) {
    return new DocumentAddress((uint)(value % 5000 + 1), (ushort)(value % 400));
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    return db;
  }

  private static void Insert(TokkDbConnection db, IEnumerable<long> values) {
    var tree = db.PrimaryIndex(Collection);
    db.InTransaction(() => {
      foreach (var value in values) {
        tree.Insert(Key(value), Address(value));
      }
    });
  }

  [Fact]
  public void AnEmptyTreeFindsNothingAndHasNoRootYet() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var tree = db.PrimaryIndex(Collection);
    Assert.True(tree.IsEmpty);
    Assert.Equal(default, tree.RootPageIndex);
    Assert.Null(tree.Find(Key(1)));
    Assert.Empty(tree.Scan());
  }

  [Fact]
  public void AKeyIsFoundAtTheAddressItWasStoredWith() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, [5, 1, 9, 3]);
    var tree = db.PrimaryIndex(Collection);
    Assert.Equal(Address(9), tree.Find(Key(9)));
    Assert.Equal(Address(1), tree.Find(Key(1)));
    Assert.Null(tree.Find(Key(4)));
  }

  //One entry per key. A column with repeated values is indexed on (value, recordId) instead,
  //which is D-3's reason for having no posting lists.
  [Fact]
  public void InsertingTheSameKeyTwiceIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, [7]);
    var tree = db.PrimaryIndex(Collection);
    Assert.Throws<DuplicateIndexKeyException>(() =>
      db.InTransaction(() => tree.Insert(Key(7), Address(7))));
  }

  //D-2: the root is a physical pointer, so it belongs in the catalogue document and nowhere
  //else. Nothing may have to scan the file to find the tree.
  [Fact]
  public void TheRootIsRecordedInTheCollectionsCatalogueDocument() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Enumerable.Range(0, 5000).Select(i => (long)i));
    var tree = db.PrimaryIndex(Collection);
    Assert.NotEqual(default, tree.RootPageIndex);
    Assert.Equal(db.Collection(Collection).PrimaryIndexRoot, tree.RootPageIndex);
  }

  [Fact]
  public void EntriesComeBackInKeyOrderWhateverOrderTheyWentInIn() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var values = Shuffled(Enumerable.Range(0, 20_000).Select(i => (long)i), seed: 4);
    Insert(db, values);

    var tree = db.PrimaryIndex(Collection);
    var scanned = tree.Scan().ToList();
    Assert.Equal(20_000, scanned.Count);
    AssertOrdered(scanned);
    Assert.Equal(values.Order().ToList(), scanned.Select(entry => DecodeSignedKey(entry.Key)).ToList());
  }

  //The structural claim. A B-tree would have entries in its interior nodes as well, and its
  //leaves would not be linked at all.
  [Fact]
  public void EveryEntryLivesInALeafAndTheLeavesAreLinkedLeftToRight() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Shuffled(Enumerable.Range(0, 40_000).Select(i => (long)i), seed: 11));

    var tree = db.PrimaryIndex(Collection);
    Assert.True(tree.Height() > 1, "the tree never grew past a single leaf");

    //Walking the chain alone reaches every entry, in order, without reading one interior node.
    var chain = tree.Leaves().ToList();
    var walked = chain.SelectMany(leaf => leaf.Entries).ToList();
    Assert.Equal(40_000, walked.Count);
    AssertOrdered(walked);
    Assert.True(chain.Count > 1, "one leaf held everything, so nothing was linked");
    Assert.All(chain, leaf => Assert.Equal(PageType.IndexLeaf, leaf.Type));
    //The last leaf ends the chain and every other one points at the next.
    Assert.Equal(default, chain[^1].NextPageIndex);
    for (var i = 0; i < chain.Count - 1; i++) {
      Assert.Equal(chain[i + 1].Index, chain[i].NextPageIndex);
    }

    //An interior node carries separators and children, and not one address.
    var interiors = tree.Nodes().OfType<IndexInteriorPage>().ToList();
    Assert.NotEmpty(interiors);
    Assert.All(interiors, node => Assert.Equal(node.Entries.Count + 1, node.ChildCount));
    Assert.Equal(chain.Count, tree.Nodes().OfType<IndexLeafPage>().Count());
  }

  [Fact]
  public void ARangeReturnsExactlyTheKeysInsideItAndStopsThere() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Enumerable.Range(0, 20_000).Select(i => (long)i));

    var tree = db.PrimaryIndex(Collection);
    var inside = tree.Range(Key(7_500), Key(7_600)).Select(entry => DecodeSignedKey(entry.Key)).ToList();
    Assert.Equal(Enumerable.Range(7_500, 100).Select(i => (long)i), inside);

    //Half open at both ends: an open start reads from the first key, an open end to the last.
    Assert.Equal(20_000, tree.Range(null, null).Count());
    Assert.Equal(500, tree.Range(null, Key(500)).Count());
    Assert.Equal(500, tree.Range(Key(19_500), null).Count());
    Assert.Empty(tree.Range(Key(20_000), null));
  }

  //Negative keys are where an encoding that left integers in two's complement would put the
  //tree in the wrong order without any test of the encoder noticing.
  [Fact]
  public void KeysOnBothSidesOfZeroSortIntoOneSequence() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Shuffled(Enumerable.Range(-10_000, 20_000).Select(i => (long)i), seed: 2));

    var scanned = db.PrimaryIndex(Collection).Scan().ToList();
    AssertOrdered(scanned);
    Assert.Equal(-10_000, DecodeSignedKey(scanned[0].Key));
    Assert.Equal(9_999, DecodeSignedKey(scanned[^1].Key));
  }

  [Fact]
  public void DeletingAKeyRemovesItAndLeavesTheOthersFindable() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Enumerable.Range(0, 10_000).Select(i => (long)i));

    var tree = db.PrimaryIndex(Collection);
    db.InTransaction(() => {
      for (var value = 0; value < 10_000; value += 2) {
        Assert.True(tree.Delete(Key(value)));
      }
    });

    //A key that is already gone is not an error, it is simply not there.
    db.InTransaction(() => Assert.False(db.PrimaryIndex(Collection).Delete(Key(2))));
    var survivors = db.PrimaryIndex(Collection).Scan().Select(entry => DecodeSignedKey(entry.Key)).ToList();
    Assert.Equal(5_000, survivors.Count);
    Assert.All(survivors, value => Assert.True(value % 2 == 1));
    AssertOrdered(db.PrimaryIndex(Collection).Scan().ToList());
  }

  //Deleting almost everything has to pull the tree back down again: nodes merge, the root
  //loses its separators, and the height falls. A tree that only ever split would keep a
  //spine of one-child nodes and read them on every lookup for ever.
  [Fact]
  public void DeletionsMergeNodesAndShrinkTheTree() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    //Enough keys for three levels: one interior node addresses some five hundred leaves, so
    //a shallower tree would never merge an interior node at all.
    Insert(db, Enumerable.Range(0, 300_000).Select(i => (long)i));

    var tree = db.PrimaryIndex(Collection);
    var heightWhenFull = tree.Height();
    var leavesWhenFull = tree.Leaves().Count();
    var interiorsWhenFull = tree.Nodes().OfType<IndexInteriorPage>().Count();
    Assert.True(heightWhenFull >= 3, $"the tree was only {heightWhenFull} deep to start with");

    db.InTransaction(() => {
      for (var value = 0; value < 300_000; value++) {
        if (value % 20 != 0) {
          Assert.True(tree.Delete(Key(value)));
        }
      }
    });

    var after = db.PrimaryIndex(Collection);
    var leavesWhenEmpty = after.Leaves().Count();
    var interiorsWhenEmpty = after.Nodes().OfType<IndexInteriorPage>().Count();
    _output.WriteLine($"height {heightWhenFull} -> {after.Height()}, " +
      $"leaves {leavesWhenFull} -> {leavesWhenEmpty}, interiors {interiorsWhenFull} -> {interiorsWhenEmpty}");
    Assert.True(after.Height() < heightWhenFull, "the tree stayed as deep as it was when full");
    Assert.True(leavesWhenEmpty < leavesWhenFull / 2, "the leaves never merged");
    Assert.True(interiorsWhenEmpty < interiorsWhenFull, "no interior node ever merged");

    var survivors = after.Scan().Select(entry => DecodeSignedKey(entry.Key)).ToList();
    Assert.Equal(15_000, survivors.Count);
    AssertOrdered(after.Scan().ToList());
    Assert.All(survivors, value => Assert.Equal(0, value % 20));
  }

  //A merge that leaked its page would make a delete-heavy workload grow the file for ever,
  //which is the property ST-1 already bought for data pages.
  [Fact]
  public void PagesAMergeRetiredAreUsedAgainBeforeTheFileGrows() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Insert(db, Enumerable.Range(0, 40_000).Select(i => (long)i));
    var pagesAfterTheFirstBuild = file.PageCount;

    var tree = db.PrimaryIndex(Collection);
    var offset = 0L;
    for (var round = 1; round <= 3; round++) {
      var previous = offset;
      db.InTransaction(() => {
        for (var value = 0; value < 40_000; value++) {
          Assert.True(tree.Delete(Key(value + previous)));
        }
      });
      //A different range each round, so the tree is genuinely rebuilt rather than refilled
      //into the pages the keys came out of.
      offset = round * 100_000L;
      var current = offset;
      db.InTransaction(() => {
        for (var value = 0; value < 40_000; value++) {
          tree.Insert(Key(value + current), Address(value));
        }
      });
    }

    _output.WriteLine($"pages {pagesAfterTheFirstBuild} -> {file.PageCount}");
    //Rebuilding the same tree three times over must not cost three more trees' worth of file.
    Assert.True(file.PageCount < pagesAfterTheFirstBuild * 2,
      $"the file grew from {pagesAfterTheFirstBuild} to {file.PageCount} pages over three rebuilds");
    Assert.Equal(40_000, db.PrimaryIndex(Collection).Scan().Count());
  }

  //A page a merge retires can be handed straight back to a split in the same transaction, so
  //the transaction ends up holding two objects for one page index. Only the second one is
  //the page; committing both would write them in whatever order the page set kept them.
  [Fact]
  public void APageRetiredAndTakenBackInOneTransactionIsWrittenOnceAndCorrectly() {
    using var file = new TempDatabaseFile();
    using (var db = NewDatabase(file)) {
      Insert(db, Enumerable.Range(0, 30_000).Select(i => (long)i));
      var tree = db.PrimaryIndex(Collection);
      db.InTransaction(() => {
        //Empties most of the tree, which retires pages...
        for (var value = 0; value < 25_000; value++) {
          tree.Delete(Key(value));
        }
        //...and then fills it again inside the same transaction, which takes them back.
        for (var value = 100_000; value < 125_000; value++) {
          tree.Insert(Key(value), Address(value));
        }
      });
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var after = reopened.PrimaryIndex(Collection);
    var expected = Enumerable.Range(25_000, 5_000).Select(i => (long)i)
      .Concat(Enumerable.Range(100_000, 25_000).Select(i => (long)i));
    AssertScanIsExactly(after, expected);
  }

  //The index is on disk, not in memory. Reopening the file must find the tree where the
  //catalogue says it is, without a scan — that is the difference NFR-2's 500 ms open depends
  //on, and what keeps a crash from taking the index with it.
  [Fact]
  public void TheTreeIsReadBackFromTheFileRatherThanRebuiltAtOpen() {
    using var file = new TempDatabaseFile();
    uint rootWhenWritten;
    using (var db = NewDatabase(file)) {
      Insert(db, Shuffled(Enumerable.Range(0, 30_000).Select(i => (long)i), seed: 7));
      rootWhenWritten = db.PrimaryIndex(Collection).RootPageIndex;
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var tree = reopened.PrimaryIndex(Collection);
    Assert.Equal(rootWhenWritten, tree.RootPageIndex);

    //A lookup after the reopen reads the few pages of a descent, not the whole index.
    var readsBeforeTheLookup = reopened.PageReadCount;
    Assert.Equal(Address(21_234), tree.Find(Key(21_234)));
    var pagesRead = reopened.PageReadCount - readsBeforeTheLookup;
    _output.WriteLine($"height {tree.Height()}, {pagesRead} pages read for one lookup");
    Assert.True(pagesRead <= tree.Height(), $"one lookup read {pagesRead} pages");
    Assert.Equal(30_000, tree.Scan().Count());
  }

  //TX-1 and TX-3: index pages are journalled like any other page, so a transaction that
  //rolls back takes its splits with it.
  [Fact]
  public void ARolledBackInsertLeavesTheTreeExactlyAsItWas() {
    using var file = new TempDatabaseFile();
    uint rootBefore;
    using (var db = NewDatabase(file)) {
      Insert(db, Enumerable.Range(0, 20_000).Select(i => (long)i));
      rootBefore = db.PrimaryIndex(Collection).RootPageIndex;

      var tree = db.PrimaryIndex(Collection);
      //Enough new keys to split the tree several times over, so what is rolled back is a
      //change of shape and not only a change of content.
      Assert.Throws<InvalidOperationException>(() => db.InTransaction(() => {
        for (var value = 20_000; value < 40_000; value++) {
          tree.Insert(Key(value), Address(value));
        }
        throw new InvalidOperationException("rolled back on purpose");
      }));
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var after = reopened.PrimaryIndex(Collection);
    Assert.Equal(rootBefore, after.RootPageIndex);
    Assert.Equal(20_000, after.Scan().Count());
    Assert.Null(after.Find(Key(30_000)));
    AssertOrdered(after.Scan().ToList());
  }


  //The done-when of this step, at the scale it was asked for. Built in batches so that the
  //tree is written to the file and read back many times over on the way, rather than being
  //assembled once in memory and saved at the end — which is the thing this index is not.
  [Fact]
  public void AMillionKeysBuildSplitMergeAndTraverseInOrder() {
    const int count = 1_000_000;
    const int batch = 100_000;
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var tree = db.PrimaryIndex(Collection);
    var clock = System.Diagnostics.Stopwatch.StartNew();

    //Shuffled, so keys land in the middle of leaves all over the tree and splits happen
    //everywhere. Inserting in order would only ever split the rightmost leaf.
    var values = Shuffled(Enumerable.Range(0, count).Select(i => (long)i), seed: 99);
    for (var start = 0; start < count; start += batch) {
      var slice = values.GetRange(start, batch);
      db.InTransaction(() => {
        foreach (var value in slice) {
          tree.Insert(Key(value), Address(value));
        }
      });
    }
    var built = clock.Elapsed;
    var heightWhenFull = tree.Height();
    var leavesWhenFull = tree.Leaves().Count();
    _output.WriteLine($"built {count:N0} keys in {built.TotalSeconds:F1} s: " +
      $"height {heightWhenFull}, {leavesWhenFull:N0} leaves, {file.PageCount:N0} pages, " +
      $"{file.Length / 1024 / 1024:N0} MB");
    Assert.True(heightWhenFull >= 3, $"a million keys made a tree only {heightWhenFull} deep");

    //Every key is there, once, in order, and the traversal is a walk of the leaf chain.
    AssertScanIsExactly(tree, Enumerable.Range(0, count).Select(i => (long)i));

    //Nine keys in ten deleted, which merges leaves and interior nodes all the way up.
    clock.Restart();
    for (var start = 0; start < count; start += batch) {
      var slice = values.GetRange(start, batch).Where(value => value % 10 != 0).ToList();
      db.InTransaction(() => {
        foreach (var value in slice) {
          Assert.True(tree.Delete(Key(value)));
        }
      });
    }
    var deleted = clock.Elapsed;
    _output.WriteLine($"deleted {count * 9 / 10:N0} keys in {deleted.TotalSeconds:F1} s: " +
      $"height {heightWhenFull} -> {tree.Height()}, " +
      $"leaves {leavesWhenFull:N0} -> {tree.Leaves().Count():N0}, {file.PageCount:N0} pages");
    Assert.True(tree.Height() < heightWhenFull, "the tree never got shorter");
    Assert.True(tree.Leaves().Count() < leavesWhenFull / 5, "the leaves never merged");

    AssertScanIsExactly(tree, Enumerable.Range(0, count).Select(i => (long)i).Where(v => v % 10 == 0));
  }

  //Streams both sides rather than materialising a million entries: the traversal is checked
  //for order, for count and for holding exactly the keys expected, in one pass.
  private static void AssertScanIsExactly(BPlusTree tree, IEnumerable<long> expected) {
    byte[] previous = null;
    var seen = 0;
    using var wanted = expected.GetEnumerator();
    foreach (var entry in tree.Scan()) {
      if (previous != null && KeyComparer.Compare(previous, entry.Key) >= 0) {
        Assert.Fail($"entry {seen} is not above the one before it");
      }
      if (!wanted.MoveNext()) {
        Assert.Fail($"the traversal returned more than the {seen} keys expected");
      }
      if (DecodeSignedKey(entry.Key) != wanted.Current) {
        Assert.Fail($"entry {seen} is {DecodeSignedKey(entry.Key)}, expected {wanted.Current}");
      }
      previous = entry.Key;
      seen++;
    }
    Assert.False(wanted.MoveNext(), $"the traversal stopped after {seen} keys");
  }

  private static List<long> Shuffled(IEnumerable<long> values, int seed) {
    var list = values.ToList();
    var random = new Random(seed);
    for (var i = list.Count - 1; i > 0; i--) {
      var j = random.Next(i + 1);
      (list[i], list[j]) = (list[j], list[i]);
    }
    return list;
  }

  private static void AssertOrdered(IReadOnlyList<IndexEntry> entries) {
    for (var i = 1; i < entries.Count; i++) {
      Assert.True(KeyComparer.Compare(entries[i - 1].Key, entries[i].Key) < 0,
        $"entry {i} is not above the one before it");
    }
  }

  //The inverse of KeyEncoder.Encode(long): the tag byte, then the sign bit back where it was.
  private static long DecodeSignedKey(byte[] key) {
    var bits = 0UL;
    for (var i = 1; i < key.Length; i++) {
      bits = (bits << 8) | key[i];
    }
    return (long)(bits ^ 0x8000000000000000);
  }

}
