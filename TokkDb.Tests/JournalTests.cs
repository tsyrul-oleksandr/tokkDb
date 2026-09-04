using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Transactions;
using TokkDb.Transactions;
using Xunit;

namespace TokkDb.Tests;

//A page manager that stops where a killed process would: the journal is on the device, the
//database file has not been touched yet.
public class KilledAfterJournalFlushPageManager : PageManager {
  public KilledAfterJournalFlushPageManager(DiskManager diskManager) : base(diskManager) { }

  protected override void WritePages(BasePage[] pages) {
    throw new SimulatedCrashException();
  }
}

public class SimulatedCrashException : Exception { }

public class JournalTests {
  private static DataPage NewPage(PageManager pageManager, uint index, int marker) {
    var page = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, index);
    page.RegisterItem(4).WriteInt(marker, 0, out _);
    return page;
  }

  [Fact]
  public void TheJournalSitsBesideTheDatabaseFile() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());

    Assert.Equal(file.Path + Journal.FileExtension, Journal.GetJournalPath(file.Path));
    Assert.True(File.Exists(file.Path + Journal.FileExtension));
  }

  [Fact]
  public void ACommittedTransactionLeavesACommittedFrame() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    var transactionManager = new TransactionManager(pageManager);

    var transaction = transactionManager.CreateTransaction();
    transaction.Track(NewPage(pageManager, 0, 11));
    transaction.Track(NewPage(pageManager, 1, 22));
    transaction.Commit();

    var frame = disk.Journal.Read();
    Assert.NotNull(frame);
    Assert.True(frame.IsComplete);
    Assert.True(frame.IsCommitted);
    Assert.Equal(transaction.Id, frame.TransactionId);
    Assert.Equal(TokkConstants.DefaultPageSize, frame.PageSize);
    //Both pages were new, so the file was empty when the transaction began.
    Assert.Equal(0u, frame.OriginalPageCount);
    Assert.Equal(2, frame.Pages.Count);
    Assert.All(frame.Pages, image => Assert.True(image.IsNewPage));
  }

  [Fact]
  public void AKillBetweenTheJournalFlushAndTheDatabaseFlushLeavesAReplayableJournal() {
    using var file = new TempDatabaseFile();

    //A database with content, so the transaction that is interrupted has something to undo.
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.Entities<Person>().Insert(TestPeople.Ivan());
    }
    var databaseBeforeTheCrash = File.ReadAllBytes(file.Path);
    var pageCountBeforeTheCrash = (uint)(databaseBeforeTheCrash.Length / TokkConstants.DefaultPageSize);

    using (var disk = new DiskManager(file.Path)) {
      disk.SetPageSize(RootPage.ReadPrefix(disk.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
      var pageManager = new KilledAfterJournalFlushPageManager(disk);
      var transactionManager = new TransactionManager(pageManager);

      var transaction = transactionManager.CreateTransaction();
      //One page that already exists and one that does not: the two cases undo differently.
      var existing = pageManager.LoadPage<DataPage>(1);
      existing.RegisterItem(4).WriteInt(999, 0, out _);
      transaction.Track(existing);
      transaction.Track(NewPage(pageManager, pageCountBeforeTheCrash, 42));

      Assert.Throws<SimulatedCrashException>(transaction.Commit);
    }

    //Nothing of the transaction reached the database file.
    Assert.Equal(databaseBeforeTheCrash, File.ReadAllBytes(file.Path));

    //And the journal holds everything the next step needs to put it back.
    using var reopened = new DiskManager(file.Path);
    reopened.SetPageSize(TokkConstants.DefaultPageSize);
    var frame = reopened.Journal.Read();
    Assert.NotNull(frame);
    Assert.True(frame.IsComplete, "the journal frame was not written whole");
    Assert.False(frame.IsCommitted, "an interrupted transaction must not look committed");
    Assert.Equal(pageCountBeforeTheCrash, frame.OriginalPageCount);
    Assert.Equal(2, frame.Pages.Count);

    var existingImage = frame.Pages.Single(image => image.PageIndex == 1);
    Assert.False(existingImage.IsNewPage);
    //The before image is the page exactly as it stood before the interrupted transaction.
    Assert.Equal(
      databaseBeforeTheCrash.Skip(TokkConstants.DefaultPageSize).Take(TokkConstants.DefaultPageSize).ToArray(),
      existingImage.BeforeImage);

    var newImage = frame.Pages.Single(image => image.PageIndex == pageCountBeforeTheCrash);
    Assert.True(newImage.IsNewPage, "a page that did not exist has no before image; undo truncates instead");
  }

  [Fact]
  public void TheJournalIsDiscardedAtTheStartOfTheNextTransaction() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    var transactionManager = new TransactionManager(pageManager);

    var first = transactionManager.CreateTransaction();
    first.Track(NewPage(pageManager, 0, 1));
    first.Track(NewPage(pageManager, 1, 2));
    first.Track(NewPage(pageManager, 2, 3));
    first.Commit();
    var lengthAfterThreePages = disk.Journal.Length;

    var second = transactionManager.CreateTransaction();
    second.Track(NewPage(pageManager, 3, 4));
    second.Commit();

    //The frame of a finished transaction has nothing left to say, so it is not kept.
    Assert.True(disk.Journal.Length < lengthAfterThreePages);
    var frame = disk.Journal.Read();
    Assert.Equal(second.Id, frame.TransactionId);
    Assert.Single(frame.Pages);
  }

  [Fact]
  public void AnEmptyTransactionWritesNoJournal() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var transactionManager = new TransactionManager(new PageManager(disk));

    transactionManager.CreateTransaction().Commit();

    Assert.Equal(0, disk.Journal.Length);
    Assert.Null(disk.Journal.Read());
  }

  [Fact]
  public void ATornJournalIsReportedRatherThanGuessedAt() {
    using var file = new TempDatabaseFile();
    var journalPath = Journal.GetJournalPath(file.Path);
    using (var disk = new DiskManager(file.Path)) {
      var pageManager = new PageManager(disk);
      var transactionManager = new TransactionManager(pageManager);
      var transaction = transactionManager.CreateTransaction();
      transaction.Track(NewPage(pageManager, 0, 7));
      transaction.Commit();
    }

    var bytes = File.ReadAllBytes(journalPath);
    bytes[20] ^= 0xFF;
    File.WriteAllBytes(journalPath, bytes);

    using var reopened = new DiskManager(file.Path);
    Assert.Throws<JournalCorruptedException>(() => reopened.Journal.Read());
  }
}
