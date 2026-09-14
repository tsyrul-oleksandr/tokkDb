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
//every query, including the ones it did not make itself, the inner queries of a relation step
//included (DG-3), and the ones that failed or were cancelled (DG-3a).
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
      RelationCatalog relations, PageManager pageManager, CatalogLock catalogLock, FreeSpaceManager freeSpace) {
    _catalog = catalog;
    _indexes = indexes;
    _relations = relations;
    _catalogLock = catalogLock;
    _executor = new QueryExecutor(dataPageManager, indexes, pageManager, freeSpace);
  }

  //Raised after every query, with the access path that was chosen and what it cost. The
  //engine has no idea what a diagnostics service is — that lives in the application, which
  //the engine cannot see — so it publishes the measurement and lets the host record it.
  //
  //Raised after the lease is given back: a subscriber is the host's code, and one that changed
  //the schema from inside the event would otherwise be a query trying to upgrade its own lease.
  //Raised once per query, inner queries first and the outer last, and for a failed or cancelled
  //query with the figures it had reached.
  public event Action<QueryReport> QueryExecuted;

  //The options a query runs with when its caller gives none: the caps, the crossover and no
  //forced choice.
  public QueryOptions Options { get; set; } = QueryOptions.Default;

  //The plan on its own, for a caller that wants to know what would happen without doing it.
  //The tests of the access-path rules use this, and so does anything that wants to explain a
  //query rather than run it.
  public QueryPlan Plan(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null) {
    using var lease = _catalogLock.Read();
    return QueryPlanner.Plan(collectionName, query, ids, _indexes);
  }

  public QueryResult Run(string collectionName, NormalizedQuery query, IReadOnlyList<Ulid> ids = null,
      CancellationToken cancellation = default) {
    return Published((_, published) =>
      _executor.Execute(QueryPlanner.Plan(collectionName, query, ids, _indexes), cancellation, published));
  }

  public QueryResult Run(QueryPlan plan, CancellationToken cancellation = default) {
    return Published((_, published) => _executor.Execute(plan, cancellation, published));
  }

  //Q-11: a request planned against the catalogue as it is now. Reads no data page.
  public QueryRequestPlan Plan(string collectionName, QueryRequest request, QueryOptions options = null) {
    using var lease = _catalogLock.Read();
    return QueryRequestPlanner.Plan(collectionName, request, lease, _catalog, _indexes, _relations,
      options ?? Options);
  }

  //Planned and run under one lease, so nothing can go stale between the two.
  public QueryResult Run(string collectionName, QueryRequest request, QueryOptions options = null,
      CancellationToken cancellation = default) {
    return Published((lease, published) => {
      var plan = QueryRequestPlanner.Plan(collectionName, request, lease, _catalog, _indexes, _relations,
        options ?? Options);
      return _executor.Execute(plan, lease, cancellation, published);
    });
  }

  //A plan made earlier, run as it is — or refused, if the catalogue has moved since (QM-2a).
  public QueryResult Run(QueryRequestPlan plan, CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(plan);
    return Published((lease, published) => _executor.Execute(plan, lease, cancellation, published));
  }

  //Runs the query under a lease and raises the event for every report it finalised once the
  //lease is given back — on the way out with the result, or on the way out with the exception.
  private QueryResult Published(Func<CatalogLease, List<QueryReport>, QueryResult> run) {
    var published = new List<QueryReport>();
    QueryResult result;
    try {
      using var lease = _catalogLock.Read();
      result = run(lease, published);
    } catch {
      Publish(published);
      throw;
    }
    Publish(published);
    return result;
  }

  private void Publish(List<QueryReport> reports) {
    foreach (var report in reports) {
      QueryExecuted?.Invoke(report);
    }
  }
}
