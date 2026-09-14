using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4, stage three: index walk, bounded heap or complete sort. Owns the order source, the
//records retained and the bytes they were retained at.
//
//Over keys and addresses, never documents (OR-3). For a bounded page it keeps at most Skip + Take
//candidates and discards the rest as they arrive (OR-3a, Q-12); for a page with no Take it holds
//every match (OR-3b). Either way it is not streaming: every candidate is examined, because an
//unseen one may outrank a retained one. Nothing is sized from Skip + Take (QM-4b) — the heap
//grows as candidates arrive and can never hold more of them than there were.
internal sealed class OrderingStage {
  private readonly string _collectionName;
  private readonly RecordOrder _order;
  private readonly long _window;
  private readonly long _cap;
  //Bounded: the worst retained candidate at the top, so it is the one compared against and
  //the one evicted.
  private readonly PriorityQueue<OrderedCandidate, OrderedCandidate> _heap;
  private readonly List<OrderedCandidate> _all;
  private long _bytes;

  public OrderingStage(string collectionName, OrderSource source, RecordOrder order, long window, long capBytes) {
    _collectionName = collectionName;
    Source = source;
    _order = order;
    _window = window;
    _cap = capBytes;
    if (source == OrderSource.BoundedHeap) {
      _heap = new PriorityQueue<OrderedCandidate, OrderedCandidate>(
        Comparer<OrderedCandidate>.Create((left, right) => order.Compare(right, left)));
    } else if (source == OrderSource.CompleteSort) {
      _all = [];
    }
  }

  public OrderSource Source { get; }

  //Whether candidates pass through this stage at all. An index walk is the order already, and an
  //unordered query has none: neither retains anything.
  public bool RetainsCandidates => Source is OrderSource.BoundedHeap or OrderSource.CompleteSort;

  //High-water marks: the most candidates held at once, and the bytes they were accounted at.
  public int RecordsRetained { get; private set; }
  public long BytesRetained { get; private set; }

  public IReadOnlyList<string> MixedTypeColumns => _order?.MixedTypeColumns ?? [];

  public void Retain(in Candidate candidate, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    var keys = _order.Keys(candidate.Fields, _collectionName, candidate.Header.RecordId);
    var entry = new OrderedCandidate(keys, candidate.Header.RecordId, candidate.Row.Address);
    var bytes = RecordOrder.BytesOf(keys);
    int held;
    if (Source == OrderSource.BoundedHeap) {
      if (_heap.Count < _window) {
        _heap.Enqueue(entry, entry);
        _bytes += bytes;
      } else if (_order.Compare(entry, _heap.Peek()) < 0) {
        var evicted = _heap.Dequeue();
        _bytes -= RecordOrder.BytesOf(evicted.Keys);
        _heap.Enqueue(entry, entry);
        _bytes += bytes;
      } else {
        //Outranked by everything on the page: discarded as it goes (OR-3a).
        return;
      }
      held = _heap.Count;
    } else {
      _all.Add(entry);
      _bytes += bytes;
      held = _all.Count;
    }
    RecordsRetained = Math.Max(RecordsRetained, held);
    BytesRetained = Math.Max(BytesRetained, _bytes);
    if (_bytes > _cap) {
      throw new QueryCapExceededException(QueryCap.OrderingStage, _collectionName, _bytes, _cap);
    }
  }

  //The retained candidates in the order, first position first.
  public IEnumerable<OrderedCandidate> Drain(CancellationToken cancellation) {
    if (Source == OrderSource.BoundedHeap) {
      //The heap yields its worst first, so it is emptied from the back.
      var ordered = new OrderedCandidate[_heap.Count];
      for (var i = ordered.Length - 1; i >= 0; i--) {
        cancellation.ThrowIfCancellationRequested();
        ordered[i] = _heap.Dequeue();
      }
      return ordered;
    }
    cancellation.ThrowIfCancellationRequested();
    _all.Sort(_order);
    return _all;
  }
}
