namespace TokkDb.Disk;

//One page as the journal holds it. A page that did not exist before the transaction has no
//before image: undoing it means truncating the file, not restoring bytes.
public record JournalPageImage(uint PageIndex, byte[] BeforeImage) {
  public bool IsNewPage => BeforeImage is null;
}

//One transaction as the journal holds it. IsCommitted says which way recovery has to go:
//a committed frame is already in the database file and needs nothing, an uncommitted one
//has to be undone from the before images and the file truncated to OriginalPageCount.
public record JournalFrame(
  ulong TransactionId,
  ushort FormatVersion,
  ushort PageSize,
  uint OriginalPageCount,
  IReadOnlyList<JournalPageImage> Pages,
  bool IsCommitted,
  bool IsComplete);
