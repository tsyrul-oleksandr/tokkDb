using TokkDb.Buffer;
using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Transactions;
using TokkDb.Transactions;
using Xunit;

namespace TokkDb.Tests;

public class TransactionTests {
  private sealed class Fixture : IDisposable {
    public TempDatabaseFile File { get; } = new();
    public DiskManager Disk { get; }
    public PageManager Pages { get; }
    public TransactionManager Transactions { get; }

    public Fixture() {
      Disk = new DiskManager(File.Path);
      Pages = new PageManager(Disk);
      Transactions = new TransactionManager(Pages);
    }

    public DataPage NewPage(uint index, int marker) {
      var page = Pages.CreateNewMemoryPage<DataPage>(PageType.Data, index);
      page.RegisterItem(4).WriteInt(marker, 0, out _);
      return page;
    }

    public void Dispose() {
      Disk.Dispose();
      File.Dispose();
    }
  }

  [Fact]
  public void ATransactionCarriesAnIdAndAState() {
    using var fixture = new Fixture();

    var first = fixture.Transactions.CreateTransaction();
    Assert.Equal(TransactionState.Active, first.State);
    Assert.True(first.IsOutermost);
    first.Commit();
    Assert.Equal(TransactionState.Committed, first.State);

    var second = fixture.Transactions.CreateTransaction();
    Assert.True(second.Id > first.Id);
    second.Rollback();
    Assert.Equal(TransactionState.RolledBack, second.State);
  }

  //The identity map has to be exactly that. A page freed and handed out again inside one
  //transaction arrives as a second object for an index the transaction already holds — an
  //index page a B+Tree merge retired and a later split took back is the case that reaches
  //here — and only the newer object is the page. Keeping both would leave which of them
  //lands in the file to the order the page set happened to keep them in.
  [Fact]
  public void TrackingAPageIndexTwiceKeepsTheNewerPageAndOnlyThatOne() {
    using var fixture = new Fixture();
    var transaction = fixture.Transactions.CreateTransaction();

    var retired = fixture.NewPage(1, marker: 111);
    transaction.Track(retired);
    var takenBack = fixture.NewPage(1, marker: 222);
    transaction.Track(takenBack);

    Assert.Single(transaction.Pages);
    Assert.Same(takenBack, transaction.Pages.Single());
    Assert.Same(takenBack, fixture.Transactions.FindTrackedPage<DataPage>(1));

    transaction.Commit();
    Assert.Equal(222, fixture.Pages.LoadPage<DataPage>(1).GetItem(0).ReadInt(0, out _));
  }

  //Tracking the same object again is not a change of page and must not disturb the set.
  [Fact]
  public void TrackingTheSamePageTwiceChangesNothing() {
    using var fixture = new Fixture();
    var transaction = fixture.Transactions.CreateTransaction();
    var page = fixture.NewPage(1, marker: 7);

    transaction.Track(page);
    transaction.Track(page);

    Assert.Single(transaction.Pages);
    Assert.Same(page, transaction.Pages.Single());
  }

  [Fact]
  public void AFinishedTransactionCannotBeUsedAgain() {
    using var fixture = new Fixture();
    var transaction = fixture.Transactions.CreateTransaction();
    transaction.Commit();

    Assert.Throws<TransactionStateException>(transaction.Commit);
    Assert.Throws<TransactionStateException>(transaction.Rollback);
    Assert.Throws<TransactionStateException>(() => transaction.Track(fixture.NewPage(0, 1)));
  }

  [Fact]
  public void ANestedCommitWritesNothingUntilTheOutermostOneCommits() {
    using var fixture = new Fixture();

    var outer = fixture.Transactions.CreateTransaction();
    outer.Track(fixture.NewPage(0, 10));

    var inner = fixture.Transactions.CreateTransaction();
    inner.Track(fixture.NewPage(1, 20));
    inner.Commit();

    //The inner transaction is done, but nothing of it has reached the device.
    Assert.Equal(TransactionState.Committed, inner.State);
    Assert.Equal(0, fixture.File.Length);
    Assert.Equal(0, fixture.Disk.Journal.Length);
    //Its pages became the outer transaction's.
    Assert.Equal(2, outer.Pages.Count);
    Assert.Same(outer, fixture.Transactions.Current);

    outer.Commit();

    Assert.Equal(2, fixture.File.PageCount);
    var frame = fixture.Disk.Journal.Read();
    Assert.Equal(outer.Id, frame.TransactionId);
    Assert.True(frame.IsCommitted);
    Assert.Equal(2, frame.Pages.Count);
  }

  [Fact]
  public void ARollbackInsideANestDoomsTheWholeNest() {
    using var fixture = new Fixture();

    var outer = fixture.Transactions.CreateTransaction();
    outer.Track(fixture.NewPage(0, 10));

    var inner = fixture.Transactions.CreateTransaction();
    inner.Track(fixture.NewPage(1, 20));
    inner.Rollback();

    Assert.True(outer.IsRollbackOnly);
    //Committing half of a failed operation is exactly what TX-1 forbids.
    Assert.Throws<TransactionStateException>(outer.Commit);
    Assert.Equal(0, fixture.File.Length);

    outer.Rollback();
    Assert.Null(fixture.Transactions.Current);
  }

  [Fact]
  public void TransactionsMustFinishInnermostFirst() {
    using var fixture = new Fixture();
    var outer = fixture.Transactions.CreateTransaction();
    fixture.Transactions.CreateTransaction();

    Assert.Throws<TransactionStateException>(outer.Commit);
  }

  [Fact]
  public void APageChangedTwiceInOneNestIsTheSameObject() {
    using var fixture = new Fixture();

    var seed = fixture.Transactions.CreateTransaction();
    seed.Track(fixture.NewPage(0, 1));
    seed.Commit();

    var outer = fixture.Transactions.CreateTransaction();
    var page = fixture.Pages.LoadPage<DataPage>(0);
    outer.Track(page);

    var inner = fixture.Transactions.CreateTransaction();
    //The identity map has to see through the nest, or the inner change works from a stale copy.
    Assert.Same(page, fixture.Transactions.FindTrackedPage<DataPage>(0));
    inner.Commit();
    outer.Commit();
  }

  [Fact]
  public void ACatalogueMutationOutsideATransactionIsRefused() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
    }

    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    var transactions = new TransactionManager(pageManager);
    var rootPageManager = new RootPageManager(pageManager, transactions);
    var catalog = new CollectionCatalog(rootPageManager, transactions);
    var freeSpace = new FreeSpaceManager(pageManager, rootPageManager, catalog, transactions);
    var dataPageManager = new DataPageManager(pageManager, catalog, freeSpace, transactions);
    catalog.SetDataPageManager(dataPageManager);

    var loading = transactions.CreateTransaction();
    rootPageManager.Initialize();
    catalog.Initialize();
    loading.Commit();

    var before = catalog.Descriptors.Count;
    //DC-8: no catalogue change happens outside a transaction, and a refused one leaves the
    //cache exactly as it was.
    Assert.Throws<TransactionNotFoundException>(() => catalog.CreateCollection("Orders"));
    Assert.Equal(before, catalog.Descriptors.Count);
    Assert.False(catalog.Exists("Orders"));
  }
}
