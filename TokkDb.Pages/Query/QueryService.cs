using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query;

//DC-5 and UI-4 in one place: plan, run, and say what was done.
//
//Every query of the engine goes through here, which is what makes the reporting complete
//rather than something each call site remembers to do. A host that wants the numbers
//subscribes once — the diagnostics service of the application does exactly that — and sees
//every query, including the ones it did not make itself.
public sealed class QueryService {
  private readonly IndexCatalog _indexes;
  private readonly QueryExecutor _executor;

  public QueryService(DataPageManager dataPageManager, IndexCatalog indexes, PageManager pageManager) {
    _indexes = indexes;
    _executor = new QueryExecutor(dataPageManager, indexes, pageManager);
  }

  //Raised after every query, with the access path that was chosen and what it cost. The
  //engine has no idea what a diagnostics service is — that lives in the application, which
  //the engine cannot see — so it publishes the measurement and lets the host record it.
  public event Action<QueryReport> QueryExecuted;

  //The plan on its own, for a caller that wants to know what would happen without doing it.
  //The tests of the access-path rules use this, and so does anything that wants to explain a
  //query rather than run it.
  public QueryPlan Plan(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    return QueryPlanner.Plan(collectionName, query, ids, _indexes);
  }

  public QueryResult Run(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    return Run(Plan(collectionName, query, ids));
  }

  public QueryResult Run(QueryPlan plan) {
    var result = _executor.Execute(plan);
    QueryExecuted?.Invoke(result.Report);
    return result;
  }
}
