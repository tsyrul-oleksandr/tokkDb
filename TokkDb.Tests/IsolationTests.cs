using TokkDb.Disk;
using Xunit;

namespace TokkDb.Tests;

//TX-4: one writer, any number of readers.
public class IsolationTests {
  private static void CreateDatabase(TempDatabaseFile file) {
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.Entities<Person>().Insert(TestPeople.Ivan());
  }

  [Fact]
  public void ASecondWriterIsRejectedWithATypedException() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var writer = new TokkDbConnection(file.Path);
    writer.Load();

    var exception = Assert.Throws<DatabaseLockedException>(() => new TokkDbConnection(file.Path));
    Assert.Equal(file.Path, exception.DatabaseFilePath);
    Assert.Contains("one writer at a time", exception.Message);
  }

  [Fact]
  public void TheWriteLockIsReleasedWhenTheWriterCloses() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using (var writer = new TokkDbConnection(file.Path)) {
      writer.Load();
      Assert.Throws<DatabaseLockedException>(() => new TokkDbConnection(file.Path));
    }

    using var next = new TokkDbConnection(file.Path);
    next.Load();
    Assert.Equal(TokkDbAccessMode.ReadWrite, next.AccessMode);
    Assert.Equal("Ivan", Assert.Single(next.Entities<Person>().GetAll()).Name);
  }

  [Fact]
  public void ReadersOpenAlongsideTheWriterAndAlongsideEachOther() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var writer = new TokkDbConnection(file.Path);
    writer.Load();

    using var firstReader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    using var secondReader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    firstReader.Load();
    secondReader.Load();

    Assert.Equal(TokkDbAccessMode.ReadOnly, firstReader.AccessMode);
    Assert.Equal("Ivan", Assert.Single(firstReader.Entities<Person>().GetAll()).Name);
    Assert.Equal("Ivan", Assert.Single(secondReader.Entities<Person>().GetAll()).Name);
    Assert.Equal(SystemCollectionCount + 1, secondReader.Collections.Count);
  }

  [Fact]
  public void AReaderRefusesToWrite() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();

    Assert.Throws<ReadOnlyDatabaseException>(() => reader.Entities<Person>().Insert(TestPeople.Numbered(2)));
    Assert.Throws<ReadOnlyDatabaseException>(() => reader.CreateCollection<Tag>());
  }

  [Fact]
  public void AReaderDoesNotTakeTheWriteLock() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using (var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly)) {
      reader.Load();
      //A writer may still open while readers are attached.
      using var writer = new TokkDbConnection(file.Path);
      writer.Load();
      writer.Entities<Person>().Insert(TestPeople.Numbered(7));
    }

    using var check = new TokkDbConnection(file.Path);
    check.Load();
    Assert.Equal(2, check.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void AReaderWillNotOpenADatabaseThatNeedsRecovering() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);
    InterruptedTransaction.Interrupt(file);

    //A reader cannot roll the interrupted transaction back, so it refuses rather than read
    //a database that is half written.
    var exception = Assert.Throws<RecoveryFailedException>(
      () => new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly));
    Assert.Contains("reader cannot", exception.Message);

    //A writer opens it, recovers it, and then the reader is happy.
    using (var writer = new TokkDbConnection(file.Path)) {
      Assert.Equal(RecoveryOutcome.UncommittedTransactionRolledBack, writer.RecoveryDecision.Outcome);
    }

    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    Assert.Equal("Ivan", Assert.Single(reader.Entities<Person>().GetAll()).Name);
  }

  private static int SystemCollectionCount => Pages.SystemCollections.All.Count;
}
