using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TokkDb.Buffer;
using TokkDb.Configuration;

namespace TokkDb.Disk;

//Owns the file handles the connection uses: the database, its journal, and — for a writer —
//the lock that makes it the only one. They stay open until the connection is disposed
//instead of being reopened for every page.
public class DiskManager : IDisposable {
  private readonly FileStream _stream;
  private readonly WriteLock _writeLock;
  private readonly ILogger _logger;
  private bool _disposed;

  public string FilePath { get; }
  public TokkDbAccessMode AccessMode { get; }

  public DiskReader Reader { get; }
  public DiskWriter Writer { get; }
  public Journal Journal { get; }
  public ushort PageSize { get; private set; }
  public long PageReadCount => Reader.PageReadCount;

  public DiskManager(string filePath, ushort pageSize = TokkConstants.DefaultPageSize,
      TokkDbAccessMode accessMode = TokkDbAccessMode.ReadWrite, ILogger logger = null) {
    FilePath = filePath;
    AccessMode = accessMode;
    PageSize = pageSize;
    _logger = logger ?? NullLogger.Instance;
    //The lock comes first: a second writer must be turned away before it opens anything.
    _writeLock = accessMode == TokkDbAccessMode.ReadWrite ? new WriteLock(filePath) : null;
    try {
      //bufferSize 1 disables the user space buffer: pages reach the operating system as they
      //are written, and durability is what Flush is for.
      _stream = accessMode == TokkDbAccessMode.ReadWrite
        ? new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1)
        : new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
      Reader = new DiskReader(_stream, pageSize);
      Writer = new DiskWriter(_stream, pageSize);
      Journal = new Journal(filePath, pageSize, accessMode);
    } catch {
      _writeLock?.Dispose();
      throw;
    }
  }

  //How many whole pages the file holds. Recorded in the journal so that a transaction which
  //grew the file can be undone by truncating it back.
  public uint PageCount => (uint)(_stream.Length / PageSize);

  //The page size of an existing file is stored in its root page, so it is known only after
  //the first bytes of the file have been read.
  public void SetPageSize(ushort pageSize) {
    PageSize = pageSize;
    Reader.PageSize = pageSize;
    Writer.PageSize = pageSize;
    Journal.PageSize = pageSize;
  }

  //Step one of the commit protocol: what the pages about to be written looked like before,
  //on the device, before the database file is touched at all.
  public void WriteJournal(ulong transactionId, IReadOnlyCollection<uint> pageIndexes) {
    RequireWritable();
    var originalPageCount = PageCount;
    Journal.Begin(transactionId, originalPageCount, pageIndexes.Count);
    foreach (var pageIndex in pageIndexes) {
      var beforeImage = pageIndex < originalPageCount ? Reader.ReadPage(pageIndex).ToArray() : null;
      Journal.WriteBeforeImage(pageIndex, beforeImage);
    }
    Journal.Flush();
  }

  //The last step, once the database file itself is durable.
  public void CommitJournal(ulong transactionId) {
    Journal.MarkCommitted(transactionId);
  }

  public bool IsBlank() {
    return Reader.IsBlank();
  }

  //The first bytes of the file, read without knowing the page size yet.
  public BufferSlice ReadPrefix(int length) {
    return Reader.ReadPrefix(length);
  }

  public PageBuffer ReadPage(uint index) {
    return Reader.ReadPage(index);
  }

  public void WritePage(PageBuffer page) {
    RequireWritable();
    Writer.WritePage(page);
  }

  //TX-2. Run before anything reads a page, so that no caller ever sees a database that an
  //interrupted transaction left half written.
  public RecoveryDecision Recover() {
    var frame = ReadFrameForRecovery();
    if (frame is null) {
      return Decide(RecoveryOutcome.NothingToRecover, 0, 0, 0, "no journal to act on");
    }
    if (frame.IsCommitted) {
      DiscardJournal();
      return Decide(RecoveryOutcome.CommittedTransactionKept, frame.TransactionId, 0, PageCount,
        "the last transaction reached its commit record, so the database file already holds it");
    }
    if (!frame.IsComplete) {
      DiscardJournal();
      return Decide(RecoveryOutcome.IncompleteJournalDiscarded, frame.TransactionId, 0, PageCount,
        "the journal stops before its before images were whole, so they never became durable and " +
        "the database file was never written to");
    }
    if (AccessMode != TokkDbAccessMode.ReadWrite) {
      throw new RecoveryFailedException(FilePath,
        $"transaction {frame.TransactionId} was interrupted and has to be rolled back, which a " +
        $"reader cannot do. Open the database for writing once to recover it.");
    }
    return Undo(frame);
  }

  //TX-3. The same undo, asked for by a transaction whose commit failed part way through its
  //writes rather than by a process that died.
  public RecoveryDecision RollbackTransaction(ulong transactionId) {
    if (AccessMode != TokkDbAccessMode.ReadWrite) {
      //A reader cannot have written anything, so there is nothing of it to take back.
      return new RecoveryDecision(RecoveryOutcome.NothingToRecover, transactionId, 0, PageCount,
        "a read-only connection changed nothing");
    }
    var frame = ReadFrameForRecovery();
    if (frame is null || frame.TransactionId != transactionId || frame.IsCommitted || !frame.IsComplete) {
      //Either nothing of this transaction reached the file, or it is not this transaction's
      //frame. Dropping the in-memory pages is the whole of the rollback.
      return Decide(RecoveryOutcome.NothingToRecover, transactionId, 0, PageCount,
        "no durable trace of the transaction to take back out");
    }
    return Undo(frame);
  }

  //Undo is idempotent: it writes the same bytes back however many times it runs, so being
  //interrupted itself costs nothing.
  private RecoveryDecision Undo(JournalFrame frame) {
    var restored = 0;
    foreach (var image in frame.Pages.Where(image => !image.IsNewPage)) {
      WritePageBytes(image.PageIndex, image.BeforeImage, frame.PageSize);
      restored++;
    }
    //Pages the transaction appended have no before image; they go by shortening the file.
    _stream.SetLength((long)frame.OriginalPageCount * frame.PageSize);
    Writer.Flush();
    DiscardJournal();
    return Decide(RecoveryOutcome.UncommittedTransactionRolledBack, frame.TransactionId, restored,
      frame.OriginalPageCount,
      $"transaction {frame.TransactionId} never committed, so its {restored} before images were " +
      $"restored and the file was truncated to {frame.OriginalPageCount} pages");
  }

  private JournalFrame ReadFrameForRecovery() {
    try {
      return Journal.Read();
    } catch (JournalCorruptedException exception) {
      //Fail closed: an unreadable journal leaves no way to tell whether the database file is
      //whole, and guessing is the one thing recovery must not do.
      throw new RecoveryFailedException(FilePath, $"its journal cannot be read ({exception.Message})", exception);
    }
  }

  private void DiscardJournal() {
    if (AccessMode == TokkDbAccessMode.ReadWrite) {
      Journal.Discard();
    }
  }

  private void WritePageBytes(uint pageIndex, byte[] bytes, ushort pageSize) {
    _stream.Position = (long)pageIndex * pageSize;
    _stream.Write(bytes, 0, bytes.Length);
  }

  private RecoveryDecision Decide(RecoveryOutcome outcome, ulong transactionId, int restoredPageCount,
      uint truncatedToPageCount, string reason) {
    var decision = new RecoveryDecision(outcome, transactionId, restoredPageCount, truncatedToPageCount, reason);
    _logger.LogInformation("Recovery of {DatabaseFilePath}: {Outcome} — {Reason}.", FilePath, outcome, reason);
    return decision;
  }

  private void RequireWritable() {
    if (AccessMode != TokkDbAccessMode.ReadWrite) {
      throw new ReadOnlyDatabaseException(FilePath);
    }
  }

  //Durability point. Only a committing transaction may call it.
  public void Flush() {
    RequireWritable();
    Writer.Flush();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    Journal.Dispose();
    _stream.Dispose();
    _writeLock?.Dispose();
  }
}
