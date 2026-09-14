using System.Collections.Immutable;

namespace TokkDb.Pages.Query;

//DG-1: how the order of a result was produced.
public enum OrderSource {
  //No order was asked for: the records come in whatever order the access path yields.
  None,

  //OR-2: the access path walks an index whose key order the requested order is a prefix of, so
  //the walk is the order and the ordering stage retains nothing.
  IndexWalk,

  //OR-3a: the ordering stage kept at most Skip + Take records by the order and discarded the
  //rest as they arrived. Memory-bounded, and not streaming: every candidate was still examined.
  BoundedHeap,

  //OR-3b: the ordering stage held every matching record, because the page had no Take and so
  //nothing could be discarded. This is what OR-7's cap governs.
  CompleteSort
}

//DG-3a: how an execution ended. A report exists for every outcome, because the queries that
//fail or are cancelled are the expensive ones, and the ones that would otherwise leave no trace.
public enum QueryOutcome {
  Completed,
  Failed,
  Cancelled
}

//PG-6: what a requested total cost beyond the page. Zero when the page's own walk had already
//examined every candidate, which an ordering stage always has.
public sealed record CountingPass(long PagesRead, int RecordsExamined) {
  public override string ToString() {
    return $"{PagesRead} page reads, {RecordsExamined} records examined";
  }
}

//UI-4 and the experimental chapter: what one query actually did.
//
//It is part of the result rather than something a log happens to mention, because the
//dissertation's claim about DC-5 is a claim about these numbers — that an indexed query reads
//a handful of pages where a scan reads all of them — and a number that has to be inferred
//from a stopwatch cannot support it.
//
//Every figure is the count of one named stage of the pipeline (DG-4) and is closed when the
//execution ends (DG-5): a report a caller holds is final, whatever the caller does with the
//records afterwards.
public sealed record QueryReport(
  string CollectionName,
  string AccessPath,
  long PagesRead,
  int RecordsExamined,
  int RecordsMatched,
  int DocumentsMaterialised,
  IReadOnlyList<string> FilterColumns,
  bool HasResidual,
  TimeSpan Elapsed) {

  //The measure the "no query materialises every document" rule is checked by: how many
  //records the access path made the engine look at, against how many it kept. A scan behind
  //a selective predicate shows up here as a large gap.
  public int RecordsRejected => RecordsExamined - RecordsMatched;

  //DG-1: how the order was produced, and the planner's reason for it, carried on the report so
  //that a reader of the report alone sees why a sort happened.
  public OrderSource OrderSource { get; init; } = OrderSource.None;
  public string OrderReason { get; init; } = "";

  //OR-3 and OR-3a: the most records the ordering stage held at once, and the bytes it accounted
  //them at — order keys, identities and addresses, never documents. Skip + Take for a bounded
  //page, every match for a complete sort, nothing for a walk.
  public int RecordsRetained { get; init; }
  public long OrderingStageBytes { get; init; }

  //PG-3 and PG-5.
  public long RecordsSkipped { get; init; }
  public int RecordsReturned { get; init; }

  //DG-1: whether the plan could stop early. A walk can, ordered by an index or not; an ordering
  //stage cannot, bounded or not, because an unseen record may outrank a retained one. Bounded
  //memory and streaming are two different facts, and this is the second.
  public bool IsStreaming { get; init; } = true;

  //PG-6: the count of every matching record, when it was asked for, and what counting cost.
  public long? TotalCount { get; init; }
  public CountingPass TotalCountPass { get; init; }

  //NF-3: the descents of a tree the access path made — one per distinct value of a seek, one per
  //id of a lookup, one for a range or an ordered walk, none for a scan or a membership pass.
  public int IndexProbes { get; init; }

  //RL-4: for a key projection, how many distinct non-null keys it produced.
  public int DistinctKeys { get; init; }

  //DG-2 and Q-10: the report of each inner query, in the order they ran, nested rather than
  //folded into the figures above. The outer figures exclude them.
  public ImmutableArray<QueryReport> InnerReports { get; init; } = [];

  public QueryOutcome Outcome { get; init; } = QueryOutcome.Completed;

  //OR-6: the order columns that held values of more than one type, which group by type in the
  //tag order of the key encoding.
  public IReadOnlyList<string> MixedTypeColumns { get; init; } = [];

  public override string ToString() {
    var line = $"{CollectionName}: {AccessPath}; " +
      $"{PagesRead} page reads, {RecordsExamined} records examined, {RecordsMatched} matched, " +
      $"{DocumentsMaterialised} documents materialised, {Elapsed.TotalMilliseconds:F2} ms";
    if (Outcome != QueryOutcome.Completed) {
      line += $"; {Outcome.ToString().ToLowerInvariant()} with the figures so far";
    }
    if (FilterColumns.Count > 0) {
      line += $"; re-checked {string.Join(", ", FilterColumns)}";
    }
    if (HasResidual) {
      line += "; residual applied per record";
    }
    if (IndexProbes > 0) {
      line += $"; {IndexProbes} index probes";
    }
    line += OrderSource switch {
      OrderSource.IndexWalk => "; order from the index walk, nothing retained",
      OrderSource.BoundedHeap => $"; order from a bounded heap that retained {RecordsRetained} records ({OrderingStageBytes} bytes)",
      OrderSource.CompleteSort => $"; order from a complete sort that held {RecordsRetained} records ({OrderingStageBytes} bytes)",
      _ => ""
    };
    if (MixedTypeColumns.Count > 0) {
      line += $"; mixed types in {string.Join(", ", MixedTypeColumns)}, grouped by type";
    }
    line += $"; {RecordsSkipped} skipped, {RecordsReturned} returned";
    line += IsStreaming ? "; streaming" : "; not streaming";
    if (TotalCount is { } total) {
      line += $"; total {total} (counting pass: {TotalCountPass})";
    }
    if (DistinctKeys > 0) {
      line += $"; {DistinctKeys} distinct keys projected";
    }
    foreach (var inner in InnerReports) {
      line += $"; inner [{inner}]";
    }
    return line;
  }
}
