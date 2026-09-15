namespace TokkDb.Pages.Query.Pipeline;

//Where a record in the order falls: before the page, or on it.
internal enum PagingDecision {
  Skip,
  Return
}

//DG-4, stage four: Skip and Take, inside the ordering stage rather than after it (PG-5). Owns
//records skipped and records returned.
//
//A position in 64 bits, compared against Skip and End and never added to anything (QM-4b): an
//offset past the last record is a page of nothing, not an error. A skipped record costs what the
//predicate and the order read of it, and no document (PG-2).
internal sealed class PagingStage {
  private readonly long _skip;
  private readonly long _end;
  private long _position;

  public PagingStage(long skip, long end) {
    _skip = skip;
    _end = end;
  }

  public long RecordsSkipped { get; private set; }
  public int RecordsReturned { get; private set; }

  //True once the page holds End - Skip records, which is when a walk may stop (PG-1).
  public bool IsFull => _position >= _end;

  public PagingDecision Admit() {
    var position = _position++;
    if (position < _skip) {
      RecordsSkipped++;
      return PagingDecision.Skip;
    }
    RecordsReturned++;
    return PagingDecision.Return;
  }
}
