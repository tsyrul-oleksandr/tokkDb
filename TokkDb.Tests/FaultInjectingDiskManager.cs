using TokkDb.Buffer;
using TokkDb.Disk;

namespace TokkDb.Tests;

//What the injected fault does when it fires.
public enum FaultMode {
  //Throws, and every write after it throws too. With unbuffered writes that is what a killed
  //process looks like from the file's point of view: the writes simply stop.
  Throw = 1,

  //Really ends the process. For an out-of-process harness only; it takes the test runner
  //with it if used in-process.
  FailFast
}

public class SimulatedProcessKillException : Exception {
  public int WriteNumber { get; }
  public string Step { get; }

  public SimulatedProcessKillException(int writeNumber, string step)
      : base($"Simulated process kill at write {writeNumber} ({step}).") {
    WriteNumber = writeNumber;
    Step = step;
  }
}

//A DiskManager that stops writing at a chosen point. Every write the engine can make goes
//through one of these overrides, so the fault can land anywhere in the commit protocol:
//while the journal is being written, after it is durable, part way through the pages, or
//before the commit record.
//
//Once it has fired nothing writes again, which is the part that makes it faithful: a killed
//process does not get to run its rollback either.
public class FaultInjectingDiskManager : DiskManager {
  private readonly int _failAfterWrites;
  private readonly FaultMode _mode;

  public FaultInjectingDiskManager(string filePath, int failAfterWrites = int.MaxValue,
      FaultMode mode = FaultMode.Throw) : base(filePath) {
    _failAfterWrites = failAfterWrites;
    _mode = mode;
  }

  //Every write attempted so far, whether or not it was allowed through. Run the workload with
  //no fault to learn how many there are, then aim at one of them.
  public int WriteCount { get; private set; }
  public bool HasFired { get; private set; }
  public string FiredAt { get; private set; }

  public override void WritePage(PageBuffer page) {
    Gate($"write page {page.Index}");
    base.WritePage(page);
  }

  public override void Flush() {
    Gate("flush the database file");
    base.Flush();
  }

  public override void CommitJournal(ulong transactionId) {
    Gate($"write the commit record of transaction {transactionId}");
    base.CommitJournal(transactionId);
  }

  protected override void BeginJournal(ulong transactionId, uint originalPageCount, int pageImageCount) {
    Gate($"begin the journal frame of transaction {transactionId}");
    base.BeginJournal(transactionId, originalPageCount, pageImageCount);
  }

  protected override void WriteJournalImage(uint pageIndex, byte[] beforeImage) {
    Gate($"journal the before image of page {pageIndex}");
    base.WriteJournalImage(pageIndex, beforeImage);
  }

  protected override void FlushJournal() {
    Gate("flush the journal");
    base.FlushJournal();
  }

  protected override void WritePageBytes(uint pageIndex, byte[] bytes, ushort pageSize) {
    Gate($"restore page {pageIndex}");
    base.WritePageBytes(pageIndex, bytes, pageSize);
  }

  protected override void Truncate(long length) {
    Gate($"truncate to {length} bytes");
    base.Truncate(length);
  }

  private void Gate(string step) {
    if (HasFired) {
      //A dead process performs no further writes, cleanup writes included.
      throw new SimulatedProcessKillException(WriteCount, FiredAt);
    }
    WriteCount++;
    if (WriteCount < _failAfterWrites) {
      return;
    }
    HasFired = true;
    FiredAt = step;
    if (_mode == FaultMode.FailFast) {
      Environment.FailFast($"Fault injection: killed before it could {step}.");
    }
    throw new SimulatedProcessKillException(WriteCount, step);
  }
}
