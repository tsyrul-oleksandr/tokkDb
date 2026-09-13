using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Relations;

namespace TokkDb.Pages.Query;

//DC-5 and UI-4 in one place: plan, run, and say what was done.
//
//Every query of the engine goes through here, which is what makes the reporting complete
//rather than something each call site remembers to do. A host that wants the numbers
//subscribes once — the diagnostics service of the application does exactly that — and sees
//every query, including the ones it did not make itself.
//
//Every plan is made and every plan is run under a read lease on the catalogue (QM-2b), so a
//schema change waits for the query rather than interleaving with it.
public sealed class QueryService {
  private readonly CollectionCatalog _catalog;
  private readonly IndexCatalog _indexes;
  private readonly RelationCatalog _relations;
  private readonly CatalogLock _catalogLock;
  private readonly QueryExecutor _executor;

  public QueryService(DataPageManager dataPageManager, CollectionCatalog catalog, IndexCatalog indexes,
      RelationCatalog relations, PageManager pageManager, CatalogLock catalogLock) {
    _catalog = catalog;
    _indexes = indexes;
    _relations = relations;
    _catalogLock = catalogLock;
    _executor = new QueryExecutor(dataPageManager, indexes, pageManager);
  }

  //Raised after every query, with the access path that was chosen and what it cost. The
  //engine has no idea what a diagnostics service is — that lives in the application, which
  //the engine cannot see — so it publishes the measurement and lets the host record it.
  //
  //Raised after the lease is given back: a subscriber is the host's code, and one that changed
  //the schema from inside the event would otherwise be a query trying to upgrade its own lease.
  public event Action<QueryReport> QueryExecuted;

  //The plan on its own, for a caller that wants to know what would happen without doing it.
  //The tests of the access-path rules use this, and so does anything that wants to explain a
  //query rather than run it.
  public QueryPlan Plan(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    using var lease = _catalogLock.Read();
    return QueryPlanner.Plan(collectionName, query, ids, _indexes);
  }

  public QueryResult Run(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    QueryResult result;
    using (_catalogLock.Read()) {
      result = _executor.Execute(QueryPlanner.Plan(collectionName, query, ids, _indexes));
    }
    return Published(result);
  }

  public QueryResult Run(QueryPlan plan) {
    QueryResult result;
    using (_catalogLock.Read()) {
      result = _executor.Execute(plan);
    }
    return Published(result);
  }

  //Q-11: a request planned against the catalogue as it is now. Reads no data page.
  public QueryRequestPlan Plan(string collectionName, QueryRequest request) {
    using var lease = _catalogLock.Read();
    return QueryRequestPlanner.Plan(collectionName, request, lease, _catalog, _indexes, _relations);
  }

  //Planned and run under one lease, so nothing can go stale between the two.
  public QueryResult Run(string collectionName, QueryRequest request) {
    QueryResult result;
    using (var lease = _catalogLock.Read()) {
      var plan = QueryRequestPlanner.Plan(collectionName, request, lease, _catalog, _indexes, _relations);
      result = _executor.Execute(plan, lease);
    }
    return Published(result);
  }

  //A plan made earlier, run as it is — or refused, if the catalogue has moved since (QM-2a).
  public QueryResult Run(QueryRequestPlan plan) {
    ArgumentNullException.ThrowIfNull(plan);
    QueryResult result;
    using (var lease = _catalogLock.Read()) {
      result = _executor.Execute(plan, lease);
    }
    return Published(result);
  }

  private QueryResult Published(QueryResult result) {
    QueryExecuted?.Invoke(result.Report);
    return result;
  }
}
