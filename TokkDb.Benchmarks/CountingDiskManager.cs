using TokkDb.Buffer;
using TokkDb.Disk;

namespace TokkDb.Benchmarks;

//NF-5: what a write costs at the device, counted where it happens — pages written, journal
//bytes, and the fsyncs of the commit protocol.
public sealed class CountingDiskManager(string filePath) : DiskManager(filePath) {
  public long PagesWritten { get; private set; }
  public long JournalBytes { get; private set; }
  public long Flushes { get; private set; }

  public void ResetCounters() {
    PagesWritten = 0;
    JournalBytes = 0;
    Flushes = 0;
  }

  public override void WritePage(PageBuffer page) {
    PagesWritten++;
    base.WritePage(page);
  }

  protected override void WriteJournalImage(uint pageIndex, byte[] beforeImage) {
    //A page past the file's end when the transaction began has no before image.
    JournalBytes += beforeImage?.Length ?? 0;
    base.WriteJournalImage(pageIndex, beforeImage);
  }

  public override void Flush() {
    Flushes++;
    base.Flush();
  }

  protected override void FlushJournal() {
    Flushes++;
    base.FlushJournal();
  }
}
