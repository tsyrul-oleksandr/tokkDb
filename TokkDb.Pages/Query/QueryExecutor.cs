using System.Diagnostics;
using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query;

//A record the query kept, with where it lives. The address comes back because a caller that
//goes on to update or delete the record already knows it and need not look it up again.
public sealed record QueryMatch(DocumentAddress Address, StoredRecord Record);

public sealed record QueryResult(IReadOnlyList<QueryMatch> Matches, QueryReport Report);

//Runs a plan: walks the access path, checks what the path did not settle, and reports what it
//cost.
//
//The predicates are checked against the record as it lies on the page. That is the point of
//the phase — DbEntities.GetAll deserializes every record of the collection to look at one
//field of it, and a query that rejects nine records in ten therefore pays nine times over for
//documents nobody asked for. Here a rejected record costs the fields the predicate names and
//nothing else, and only a record that survives is turned into a document.
//
//The checking is done by the same expression tree the query arrived as (DC-5). Nothing here
//interprets an operator: a BufferedObjectValue is an IFieldSource just as a parsed object is,
//so the comparison that would have run against a document runs against the buffer unchanged.
public sealed class QueryExecutor {
  private readonly DataPageManager _dataPageManager;
  private readonly IndexCatalog _indexes;
  private readonly PageManager _pageManager;

  public QueryExecutor(DataPageManager dataPageManager, IndexCatalog indexes, PageManager pageManager) {
    _dataPageManager = dataPageManager;
    _indexes = indexes;
    _pageManager = pageManager;
  }

  public QueryResult Execute(QueryPlan plan) {
    var filters = plan.Filters.Select(ToComparison).ToArray();
    var matches = new List<QueryMatch>();
    var examined = 0;
    var materialised = 0;

    //Counted around the whole walk, index descents included: the page a predicate is checked
    //on and the pages the tree was read through are the same cost to NFR-2.
    var pagesBefore = _pageManager.PageReadCount;
    var stopwatch = Stopwatch.StartNew();
    foreach (var row in Rows(plan.Path)) {
      examined++;
      //An overflowed record is put back together here and nowhere else; one that fits its
      //page is checked where it lies, with nothing copied.
      var buffer = _dataPageManager.ReadRecordBuffer(row);
      var fields = new BufferedObjectValue(buffer, RecordHeader.ByteSize);
      if (!Satisfies(filters, plan.Residual, fields)) {
        continue;
      }
      materialised++;
      matches.Add(new QueryMatch(row.Address, StoredRecordUtilities.FromBuffer(buffer)));
    }
    stopwatch.Stop();

    var report = new QueryReport(plan.CollectionName, plan.Path.Describe(),
      _pageManager.PageReadCount - pagesBefore, examined, matches.Count, materialised,
      plan.FilterColumns.ToArray(), plan.HasResidual, stopwatch.Elapsed);
    return new QueryResult(matches, report);
  }

  private static bool Satisfies(IReadOnlyList<ComparisonExpression> filters, IExpression residual,
      BufferedObjectValue fields) {
    foreach (var filter in filters) {
      if (!BooleanExpression.IsTrue(filter.Execute(fields, fields))) {
        return false;
      }
    }
    //Last, because it is the expensive half: a conjunct reads one field and a residual may
    //walk a whole subtree of the predicate.
    return residual is null || BooleanExpression.IsTrue(residual.Execute(fields, fields));
  }

  //The conjunct as the expression it was lifted out of. Rebuilding it rather than
  //interpreting the predicate keeps one evaluator for the two halves of the query, so a
  //conjunct and a residual comparing the same column can never disagree.
  private static ComparisonExpression ToComparison(QueryPredicate predicate) {
    var column = new PropertyExpression(predicate.ColumnName) { Parent = new RootExpression() };
    return new ComparisonExpression(column, predicate.Operator,
      new ConstantExpression(predicate.Constants), predicate.ColumnType);
  }

  private IEnumerable<DataRow> Rows(AccessPath path) {
    return path switch {
      PrimaryKeyPath primary => PrimaryKeyRows(primary),
      IndexSeekPath seek => SeekRows(seek),
      IndexRangePath range => RangeRows(range),
      FullScanPath scan => ScanRows(scan),
      _ => throw new NotSupportedException($"Access path '{path.GetType().Name}' cannot be executed.")
    };
  }

  private IEnumerable<DataRow> PrimaryKeyRows(PrimaryKeyPath path) {
    foreach (var id in path.Ids) {
      if (_dataPageManager.FindLiveRow(path.CollectionName, id) is { } row) {
        yield return row;
      }
    }
  }

  private IEnumerable<DataRow> SeekRows(IndexSeekPath path) {
    var index = RequireIndex(path.CollectionName, path.ColumnName);
    //An IN over several values is several descents. They are walked in the order the query
    //wrote them, so a caller that wants the results ordered has to order them; the access
    //path promises the right records, not an order.
    foreach (var value in path.Values) {
      foreach (var (_, address) in index.Find(value)) {
        if (_dataPageManager.LiveRowAt(address) is { } row) {
          yield return row;
        }
      }
    }
  }

  private IEnumerable<DataRow> RangeRows(IndexRangePath path) {
    var index = RequireIndex(path.CollectionName, path.ColumnName);
    //One descent to the lower bound and then a walk of the linked leaves — the sequential
    //read the B+Tree of Phase 5 exists to make possible.
    foreach (var entry in index.Tree.Range(path.From, path.To)) {
      if (_dataPageManager.LiveRowAt(entry.Address) is { } row) {
        yield return row;
      }
    }
  }

  private IEnumerable<DataRow> ScanRows(FullScanPath path) {
    foreach (var row in _dataPageManager.GetAllRows(path.CollectionName)) {
      //Dead images are on the page until compaction takes them; only the header is read to
      //recognise one.
      if (StoredRecordUtilities.ReadHeader(row.Buffer).IsLive) {
        yield return row;
      }
    }
  }

  private SecondaryIndex RequireIndex(string collectionName, string columnName) {
    return _indexes?.Find(collectionName, columnName)
      ?? throw new InvalidOperationException(
        $"The plan reads {collectionName}.{columnName} through an index that no longer exists.");
  }
}
