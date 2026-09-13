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
      //page is checked where it lies, with nothing copied. A record written under an older
      //schema is read through the migration on the way (DC-7), one field at a time like any
      //other — the predicate cannot tell, which is the point.
      var fields = _dataPageManager.ReadFields(plan.CollectionName, row, out _);
      if (!Satisfies(filters, plan.Residual, fields)) {
        continue;
      }
      materialised++;
      matches.Add(new QueryMatch(row.Address, _dataPageManager.ReadRecord(plan.CollectionName, row)));
    }
    stopwatch.Stop();

    var report = new QueryReport(plan.CollectionName, plan.Path.Describe(),
      _pageManager.PageReadCount - pagesBefore, examined, matches.Count, materialised,
      plan.FilterColumns.ToArray(), plan.HasResidual, stopwatch.Elapsed);
    return new QueryResult(matches, report);
  }

  //Q-11: a request's plan, run under a lease on the catalogue it was planned against.
  //
  //The version is checked before anything is read, and nothing can move it until the lease is
  //given back, so the check and the reads see one catalogue (QM-2b).
  //
  //What this does not do yet, and must not appear to: the ordering always sorts, holding a key
  //and an address for every match — never a document — where OR-2 would take the order from an
  //index walk and OR-3a would hold no more than Skip + Take. And a relation step is resolved and
  //planned but not run, so a plan carrying one is refused here rather than answered as though the
  //step were not in it.
  public QueryResult Execute(QueryRequestPlan plan, CatalogLease lease) {
    RequireCurrent(plan, lease);
    if (!plan.RelationSteps.IsEmpty) {
      throw new NotSupportedException(
        $"The plan narrows {plan.CollectionName} across {string.Join(", ", plan.RelationSteps.Select(step => step.Step.RelationName))}, " +
        $"and a relation step is planned but not yet executed. Running the query without it would return " +
        $"records the step was there to exclude.");
    }
    var access = plan.Access;
    var filters = access.Filters.Select(ToComparison).ToArray();
    var page = new List<QueryMatch>();
    var examined = 0;
    var matched = 0;

    var pagesBefore = _pageManager.PageReadCount;
    var stopwatch = Stopwatch.StartNew();
    //QM-5: a page of nothing is known to be empty before anything is read, so nothing is.
    if (plan.Take != 0) {
      if (plan.Request.IsOrdered) {
        Ordered(plan, filters, page, ref examined, ref matched);
      } else {
        Unordered(plan, filters, page, ref examined, ref matched);
      }
    }
    stopwatch.Stop();

    var report = new QueryReport(plan.CollectionName, access.Path.Describe(),
      _pageManager.PageReadCount - pagesBefore, examined, matched, page.Count,
      access.FilterColumns.ToArray(), access.HasResidual, stopwatch.Elapsed);
    return new QueryResult(page, report);
  }

  private static void RequireCurrent(QueryRequestPlan plan, CatalogLease lease) {
    if (!ReferenceEquals(plan.Catalog, lease.Owner)) {
      throw new ArgumentException(
        $"The plan for {plan.CollectionName} was made against the catalogue of another connection.", nameof(plan));
    }
    if (plan.CatalogVersion != lease.Version) {
      throw new StalePlanException(plan.CollectionName, plan.CatalogVersion, lease.Version);
    }
  }

  //PG-4: with no order any N matches are the answer, so the walk stops at the Nth. Skip needs an
  //order and a request without one cannot carry it, so the page starts at the first match.
  private void Unordered(QueryRequestPlan plan, IReadOnlyList<ComparisonExpression> filters,
      List<QueryMatch> page, ref int examined, ref int matched) {
    var access = plan.Access;
    foreach (var row in Rows(access.Path)) {
      examined++;
      var fields = _dataPageManager.ReadFields(plan.CollectionName, row, out _);
      if (!Satisfies(filters, access.Residual, fields)) {
        continue;
      }
      matched++;
      page.Add(new QueryMatch(row.Address, _dataPageManager.ReadRecord(plan.CollectionName, row)));
      if (page.Count >= plan.Request.End) {
        break;
      }
    }
  }

  //The order is applied after the conjuncts and the residual (OR-5), so what is sorted is the
  //matches and nothing else. The list grows as matches arrive rather than being reserved from
  //Skip + Take, which the caller chose (QM-4b).
  private void Ordered(QueryRequestPlan plan, IReadOnlyList<ComparisonExpression> filters,
      List<QueryMatch> page, ref int examined, ref int matched) {
    var access = plan.Access;
    var order = new RecordOrder(plan.Order);
    var candidates = new List<OrderedCandidate>();
    foreach (var row in Rows(access.Path)) {
      examined++;
      var fields = _dataPageManager.ReadFields(plan.CollectionName, row, out var header);
      if (!Satisfies(filters, access.Residual, fields)) {
        continue;
      }
      matched++;
      candidates.Add(new OrderedCandidate(order.Keys(fields, plan.CollectionName, header.RecordId),
        header.RecordId, row.Address));
    }
    candidates.Sort(order);
    //Positions stay in 64 bits and are compared, never added to: an offset past the last match is
    //an empty page and not an error (QM-4b).
    for (var position = plan.Skip; position < candidates.Count && position < plan.Request.End; position++) {
      var candidate = candidates[(int)position];
      //Read again by address, because the rows of the walk were not kept: holding them would hold
      //every page the walk passed through.
      if (_dataPageManager.LiveRowAt(candidate.Address) is { } row) {
        page.Add(new QueryMatch(candidate.Address, _dataPageManager.ReadRecord(plan.CollectionName, row)));
      }
    }
  }

  private static bool Satisfies(IReadOnlyList<ComparisonExpression> filters, IExpression residual,
      IFieldSource fields) {
    var value = (IDocumentValue)fields;
    foreach (var filter in filters) {
      if (!BooleanExpression.IsTrue(filter.Execute(value, value))) {
        return false;
      }
    }
    //Last, because it is the expensive half: a conjunct reads one field and a residual may
    //walk a whole subtree of the predicate.
    return residual is null || BooleanExpression.IsTrue(residual.Execute(value, value));
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
