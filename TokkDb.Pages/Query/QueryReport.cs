namespace TokkDb.Pages.Query;

//UI-4 and the experimental chapter: what one query actually did.
//
//It is part of the result rather than something a log happens to mention, because the
//dissertation's claim about DC-5 is a claim about these numbers — that an indexed query reads
//a handful of pages where a scan reads all of them — and a number that has to be inferred
//from a stopwatch cannot support it.
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

  public override string ToString() {
    var line = $"{CollectionName}: {AccessPath}; " +
      $"{PagesRead} page reads, {RecordsExamined} records examined, {RecordsMatched} matched, " +
      $"{DocumentsMaterialised} documents materialised, {Elapsed.TotalMilliseconds:F2} ms";
    if (FilterColumns.Count > 0) {
      line += $"; re-checked {string.Join(", ", FilterColumns)}";
    }
    if (HasResidual) {
      line += "; residual applied per record";
    }
    return line;
  }
}
