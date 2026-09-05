using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Transactions;
using TokkDb.Transactions;
using Xunit;

namespace TokkDb.Tests;

//Stops where a killed process would: the pages are in the database file, the commit record
//is not yet written.
public class KilledBeforeCommitRecordPageManager : PageManager {
  public KilledBeforeCommitRecordPageManager(DiskManager diskManager) : base(diskManager) { }

  protected override void MarkJournalCommitted(ulong transactionId) {
    throw new SimulatedCrashException();
  }
}

public static class InterruptedTransaction {
  public static byte[] CreateDatabaseWithPeople(TempDatabaseFile file, int count = 3) {
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      var entities = db.Entities<Person>();
      for (var i = 0; i < count; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
    }
    return File.ReadAllBytes(file.Path);
  }

  //Writes a transaction's pages to the database file and then dies before the commit record,
  //which is the state recovery exists for.
  public static void Interrupt(TempDatabaseFile file) {
    using var disk = new DiskManager(file.Path);
    disk.SetPageSize(RootPage.ReadPrefix(disk.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var pageManager = new KilledBeforeCommitRecordPageManager(disk);
    var transactionManager = new TransactionManager(pageManager);

    var transaction = transactionManager.CreateTransaction();
    //One page that already exists and one appended past the end: the two undo differently.
    var existing = pageManager.LoadPage<DataPage>(1);
    existing.RegisterItem(4).WriteInt(999, 0, out _);
    transaction.Track(existing);
    var appended = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, disk.PageCount);
    appended.RegisterItem(4).WriteInt(1234, 0, out _);
    transaction.Track(appended);

    Assert.Throws<SimulatedCrashException>(transaction.Commit);
  }
}

public class RecoveryTests {
  [Fact]
  public void AnInterruptedTransactionIsNeverPartiallyVisibleAfterReopening() {
    using var file = new TempDatabaseFile();
    var databaseBeforeTheCrash = InterruptedTransaction.CreateDatabaseWithPeople(file);

    InterruptedTransaction.Interrupt(file);
    //The interrupted transaction really did reach the file: this is the state recovery is for.
    Assert.NotEqual(databaseBeforeTheCrash, File.ReadAllBytes(file.Path));

    using var reopened = new TokkDbConnection(file.Path);
    //Byte for byte what it was before the transaction that never committed.
    Assert.Equal(databaseBeforeTheCrash, File.ReadAllBytes(file.Path));

    reopened.Load();
    Assert.Equal(3, reopened.Entities<Person>().GetAll().Count());
    Assert.Equal(3u, reopened.Collection("Person").RecordCount);
  }

  [Fact]
  public void TheRecoveryDecisionIsReportedAndLogged() {
    using var file = new TempDatabaseFile();
    InterruptedTransaction.CreateDatabaseWithPeople(file);
    InterruptedTransaction.Interrupt(file);

    var logger = new RecordingLogger();
    using var reopened = new TokkDbConnection(file.Path, logger: logger);

    var decision = reopened.RecoveryDecision;
    Assert.Equal(RecoveryOutcome.UncommittedTransactionRolledBack, decision.Outcome);
    Assert.Equal(1, decision.RestoredPageCount);
    Assert.NotEqual(0u, decision.TransactionId);
    Assert.Contains("never committed", decision.Reason);

    var line = Assert.Single(logger.Messages);
    Assert.Contains(nameof(RecoveryOutcome.UncommittedTransactionRolledBack), line);
    Assert.Contains(file.Path, line);
  }

  [Fact]
  public void TheJournalIsTruncatedOnceRecoveryHasRun() {
    using var file = new TempDatabaseFile();
    InterruptedTransaction.CreateDatabaseWithPeople(file);
    InterruptedTransaction.Interrupt(file);
    Assert.True(new FileInfo(Journal.GetJournalPath(file.Path)).Length > 0);

    using (var reopened = new TokkDbConnection(file.Path)) {
      Assert.Equal(RecoveryOutcome.UncommittedTransactionRolledBack, reopened.RecoveryDecision.Outcome);
    }

    Assert.Equal(0, new FileInfo(Journal.GetJournalPath(file.Path)).Length);

    //And a second open has nothing left to do.
    using var again = new TokkDbConnection(file.Path);
    Assert.Equal(RecoveryOutcome.NothingToRecover, again.RecoveryDecision.Outcome);
  }

  [Fact]
  public void ACommittedTransactionIsKeptOnOpen() {
    using var file = new TempDatabaseFile();
    var expected = InterruptedTransaction.CreateDatabaseWithPeople(file);

    using var reopened = new TokkDbConnection(file.Path);
    Assert.Equal(RecoveryOutcome.CommittedTransactionKept, reopened.RecoveryDecision.Outcome);
    Assert.Equal(expected, File.ReadAllBytes(file.Path));
    reopened.Load();
    Assert.Equal(3, reopened.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void AJournalCutShortBeforeItsImagesWereWholeIsDiscarded() {
    using var file = new TempDatabaseFile();
    var expected = InterruptedTransaction.CreateDatabaseWithPeople(file);

    //A journal whose images never became durable never let the database file be touched, so
    //there is nothing of it to undo.
    var journalPath = Journal.GetJournalPath(file.Path);
    using (var disk = new DiskManager(file.Path)) {
      disk.SetPageSize(TokkConstants.DefaultPageSize);
      disk.WriteJournal(99, [1, 2]);
    }
    var written = File.ReadAllBytes(journalPath);
    File.WriteAllBytes(journalPath, written.Take(written.Length / 2).ToArray());

    using var reopened = new TokkDbConnection(file.Path);
    Assert.Equal(RecoveryOutcome.IncompleteJournalDiscarded, reopened.RecoveryDecision.Outcome);
    Assert.Equal(expected, File.ReadAllBytes(file.Path));
  }

  [Fact]
  public void AnUnreadableJournalRefusesToOpenRatherThanGuess() {
    using var file = new TempDatabaseFile();
    InterruptedTransaction.CreateDatabaseWithPeople(file);
    InterruptedTransaction.Interrupt(file);

    var journalPath = Journal.GetJournalPath(file.Path);
    var bytes = File.ReadAllBytes(journalPath);
    bytes[20] ^= 0xFF;
    File.WriteAllBytes(journalPath, bytes);

    var exception = Assert.Throws<RecoveryFailedException>(() => new TokkDbConnection(file.Path));
    Assert.Equal(file.Path, exception.DatabaseFilePath);
    Assert.Contains("journal cannot be read", exception.Message);
  }

  [Fact]
  public void ARollbackAfterTheDatabaseWasAlreadyWrittenLeavesNoChangeVisible() {
    using var file = new TempDatabaseFile();
    var databaseBeforeTheRollback = InterruptedTransaction.CreateDatabaseWithPeople(file);

    using (var disk = new DiskManager(file.Path)) {
      disk.SetPageSize(RootPage.ReadPrefix(disk.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
      var pageManager = new KilledBeforeCommitRecordPageManager(disk);
      var transactionManager = new TransactionManager(pageManager);

      var transaction = transactionManager.CreateTransaction();
      var existing = pageManager.LoadPage<DataPage>(1);
      existing.RegisterItem(4).WriteInt(999, 0, out _);
      transaction.Track(existing);
      transaction.Track(pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, disk.PageCount));

      Assert.Throws<SimulatedCrashException>(transaction.Commit);
      //TX-3: the pages are in the file by now, so dropping the in-memory set is not a rollback.
      Assert.NotEqual(databaseBeforeTheRollback, File.ReadAllBytes(file.Path));

      transaction.Rollback();
      Assert.Equal(TransactionState.RolledBack, transaction.State);
    }

    Assert.Equal(databaseBeforeTheRollback, File.ReadAllBytes(file.Path));

    //Nothing left for recovery to find either.
    using var reopened = new TokkDbConnection(file.Path);
    Assert.Equal(RecoveryOutcome.NothingToRecover, reopened.RecoveryDecision.Outcome);
    reopened.Load();
    Assert.Equal(3, reopened.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void ARollbackWithNothingWrittenLeavesTheFileAlone() {
    using var file = new TempDatabaseFile();
    var expected = InterruptedTransaction.CreateDatabaseWithPeople(file);

    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      //An operation that fails before it commits leaves the file exactly as it was.
      Assert.Throws<ReservedCollectionNameException>(() => db.CreateCollection("_events"));
    }

    Assert.Equal(expected, File.ReadAllBytes(file.Path));
  }
}
