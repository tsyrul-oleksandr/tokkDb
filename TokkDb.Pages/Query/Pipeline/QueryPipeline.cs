using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4: the execution as explicit stages, in the order the table names them — candidate
//enumeration, predicate evaluation, ordering, paging, materialisation, report finalisation —
//with every counter owned by exactly one of them.
//
//One pipeline runs one plan over one collection: a page (Run), a key projection for a semi-join
//(Project), or the existing entry point's every-match result, which is a page with no window.
//The report it finalises carries the figures of its own stages and, nested, the reports of the
//inner queries it ran (DG-2). Laziness stays inside: the walk is an enumerator that a full page
//stops, and what leaves here is a list and a closed report (DG-5).
internal sealed class QueryPipeline {
  private readonly DataPageManager _data;
  private readonly IndexCatalog _indexes;
  private readonly PageManager _pages;
  private readonly string _collectionName;
  private readonly string _orderReason;
  private readonly CancellationToken _cancellation;

  private readonly CandidateEnumeration _enumeration;
  private readonly OrderingStage _ordering;
  private readonly PagingStage _paging;
  private readonly MaterialisationStage _materialisation;
  private readonly ReportFinalisation _finalisation;
  private PredicateEvaluation _predicate;

  private QueryPlan _access;
  private bool _walkStoppedAtPage;
  private long? _total;
  private CountingPass _totalPass;
  private int _distinctKeys;

  public QueryPipeline(DataPageManager data, IndexCatalog indexes, PageManager pages, string collectionName,
      OrderSource orderSource, string orderReason, RecordOrder order, long skip, long end, long orderingCapBytes,
      CancellationToken cancellation) {
    _data = data;
    _indexes = indexes;
    _pages = pages;
    _collectionName = collectionName;
    _orderReason = orderReason;
    _cancellation = cancellation;
    _enumeration = new CandidateEnumeration(data, indexes, pages);
    _ordering = new OrderingStage(collectionName, orderSource, order, end, orderingCapBytes);
    _paging = new PagingStage(skip, end);
    _materialisation = new MaterialisationStage(data);
    _finalisation = new ReportFinalisation();
  }

  //The reports of the inner queries this one ran, complete or partial, in the order they ran.
  public List<QueryReport> InnerReports { get; } = [];

  //The closed report, once Finalise has run.
  public QueryReport Report { get; private set; }

  //Names the plan the page will be read by, so that a report of a run that failed before its
  //walk began still says which path it was going to take.
  public void Describe(QueryPlan access) {
    _access = access;
  }

  public List<QueryMatch> Run(QueryPlan access) {
    Describe(access);
    _predicate = new PredicateEvaluation(access);
    var page = new List<QueryMatch>();
    if (_ordering.RetainsCandidates) {
      //OR-5: the order is applied after the conjuncts and the residual, so what the stage sees
      //is the matches and nothing else.
      foreach (var candidate in _enumeration.Walk(_collectionName, access.Path, _cancellation)) {
        if (_predicate.Satisfies(candidate)) {
          _ordering.Retain(candidate, _cancellation);
        }
      }
      foreach (var entry in _ordering.Drain(_cancellation)) {
        if (_paging.Admit() == PagingDecision.Return
            && _materialisation.MaterialiseAt(_collectionName, entry.Address) is { } match) {
          page.Add(match);
        }
        if (_paging.IsFull) {
          break;
        }
      }
    } else {
      //The walk is the order, or there is none: the page is taken from the walk as it comes,
      //and the walk stops the moment the page is full (PG-1). Materialised from the row in hand,
      //because the walk has the page open.
      foreach (var candidate in _enumeration.Walk(_collectionName, access.Path, _cancellation)) {
        if (!_predicate.Satisfies(candidate)) {
          continue;
        }
        if (_paging.Admit() == PagingDecision.Return) {
          page.Add(_materialisation.Materialise(_collectionName, candidate.Row));
        }
        if (_paging.IsFull) {
          _walkStoppedAtPage = true;
          break;
        }
      }
    }
    _enumeration.Close();
    return page;
  }

  //RL-4: the far column of every record matching the predicate, deduplicated into the set, nulls
  //left out (RL-10). No order, no page, no document.
  public void Project(QueryPlan access, string column, KeySet keys) {
    Describe(access);
    _predicate = new PredicateEvaluation(access);
    foreach (var candidate in _enumeration.Walk(_collectionName, access.Path, _cancellation)) {
      if (!_predicate.Satisfies(candidate)) {
        continue;
      }
      var value = candidate.Fields.GetField(column);
      if (value is null or NullDocumentValue) {
        continue;
      }
      keys.Add(Encode(value, column, candidate.Header.RecordId).Bytes);
    }
    _enumeration.Close();
    _distinctKeys = keys.Count;
  }

  //PG-6: the count of every match, at its own cost. A walk the page stopped is walked again from
  //the start, counting and materialising nothing; a walk that examined everything already knows.
  public void CountTotal(QueryPlan access) {
    if (!_walkStoppedAtPage) {
      _total = _predicate.RecordsMatched;
      _totalPass = new CountingPass(0, 0);
      return;
    }
    var counting = new CandidateEnumeration(_data, _indexes, _pages);
    var predicate = new PredicateEvaluation(access);
    foreach (var candidate in counting.Walk(_collectionName, access.Path, _cancellation)) {
      predicate.Satisfies(candidate);
    }
    counting.Close();
    _total = predicate.RecordsMatched;
    _totalPass = new CountingPass(counting.PagesRead, counting.RecordsExamined);
  }

  public QueryReport Finalise(QueryOutcome outcome) {
    _enumeration.Close();
    Report = new QueryReport(_collectionName, _access?.Path.Describe() ?? "not planned",
      _enumeration.PagesRead, _enumeration.RecordsExamined, _predicate?.RecordsMatched ?? 0,
      _materialisation.DocumentsMaterialised, _access?.FilterColumns.ToArray() ?? [],
      _access?.HasResidual ?? false, _finalisation.Close()) {
      OrderSource = _ordering.Source,
      OrderReason = _orderReason,
      RecordsRetained = _ordering.RecordsRetained,
      OrderingStageBytes = _ordering.BytesRetained,
      RecordsSkipped = _paging.RecordsSkipped,
      RecordsReturned = _paging.RecordsReturned,
      IsStreaming = !_ordering.RetainsCandidates,
      TotalCount = _total,
      TotalCountPass = _totalPass,
      IndexProbes = _enumeration.IndexProbes,
      DistinctKeys = _distinctKeys,
      InnerReports = [.. InnerReports],
      Outcome = outcome,
      MixedTypeColumns = _ordering.MixedTypeColumns
    };
    return Report;
  }

  private EncodedKey Encode(IDocumentValue value, string column, Ulid recordId) {
    try {
      return KeyEncoder.Encode(value);
    } catch (NotSupportedException inner) {
      throw new NotSupportedException(
        $"Record {recordId} of {_collectionName} holds {value.Type} in '{column}', which has no key, so " +
        $"the relation cannot be joined on it.", inner);
    }
  }
}
