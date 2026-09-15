using System.Collections.Immutable;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Query.Pipeline;

namespace TokkDb.Pages.Query;

//A record the query kept, with where it lives. The address comes back because a caller that
//goes on to update or delete the record already knows it and need not look it up again.
public sealed record QueryMatch(DocumentAddress Address, StoredRecord Record);

public sealed record QueryResult(IReadOnlyList<QueryMatch> Matches, QueryReport Report);

//Runs a plan on the pipeline of DG-4, and reports what it cost.
//
//Both entry points sit on the one pipeline (QM-3): the existing Execute(QueryPlan) is a page with
//no window and no order, and a request's plan adds the order, the page and the relation steps.
//Neither is implemented by filtering the other's result, and the executor's premise holds for
//both — only a surviving record of the page becomes a document.
//
//A relation step runs here as a key projection over the far collection (RL-4), under the same
//lease as the query it narrows (RL-12); its keys are bound into the plan, the In strategy is
//chosen by the crossover (RL-3b), and the outer walk begins only then. Every report, complete or
//partial, goes to the published list in the order it was finalised: inner first, then outer
//(DG-3, DG-3a).
public sealed class QueryExecutor {
  private readonly DataPageManager _data;
  private readonly IndexCatalog _indexes;
  private readonly PageManager _pages;
  private readonly FreeSpaceManager _freeSpace;

  public QueryExecutor(DataPageManager dataPageManager, IndexCatalog indexes, PageManager pageManager,
      FreeSpaceManager freeSpace) {
    _data = dataPageManager;
    _indexes = indexes;
    _pages = pageManager;
    _freeSpace = freeSpace;
  }

  public QueryResult Execute(QueryPlan plan) {
    return Execute(plan, CancellationToken.None, []);
  }

  //The existing entry point: every match, materialised, in the order the path yields them.
  public QueryResult Execute(QueryPlan plan, CancellationToken cancellation, List<QueryReport> published) {
    ArgumentNullException.ThrowIfNull(plan);
    PlanInvariants.RequireNoRelationStep(plan);
    var pipeline = new QueryPipeline(_data, _indexes, _pages, plan.CollectionName, OrderSource.None,
      "no order was asked for", null, 0, long.MaxValue, long.MaxValue, cancellation);
    pipeline.Describe(plan);
    var matches = Guarded(pipeline, published, null, () => pipeline.Run(plan));
    return new QueryResult(matches, pipeline.Report);
  }

  //Q-11: a request's plan, run under a lease on the catalogue it was planned against.
  //
  //The version is checked before anything is read, and nothing can move it until the lease is
  //given back, so the check and the reads see one catalogue (QM-2b).
  public QueryResult Execute(QueryRequestPlan plan, CatalogLease lease, CancellationToken cancellation,
      List<QueryReport> published) {
    ArgumentNullException.ThrowIfNull(plan);
    RequireCurrent(plan, lease);
    PlanInvariants.RequireNoRelationStep(plan.Access);
    var order = plan.Request.IsOrdered ? new RecordOrder(plan.Order) : null;
    var pipeline = new QueryPipeline(_data, _indexes, _pages, plan.CollectionName, plan.OrderSource,
      plan.OrderReason, order, plan.Skip, plan.Request.End, plan.Options.OrderingStageCapBytes, cancellation);
    pipeline.Describe(plan.Access);
    var page = Guarded(pipeline, published, null, () => {
      //QM-5: a page of nothing is known to be empty before anything is read, so nothing is — not
      //even an inner query.
      if (plan.Take == 0) {
        return [];
      }
      var bound = plan.Access;
      if (!plan.RelationSteps.IsEmpty) {
        var projected = new Dictionary<KeySet, KeySet>(ReferenceEqualityComparer.Instance);
        foreach (var step in plan.RelationSteps) {
          projected[step.Keys] = Project(step, plan, lease, cancellation, pipeline.InnerReports, published);
        }
        bound = Bind(plan, projected);
        pipeline.Describe(bound);
      }
      var matches = pipeline.Run(bound);
      if (plan.Request.IncludeTotal) {
        pipeline.CountTotal(bound);
      }
      return matches;
    });
    return new QueryResult(page, pipeline.Report);
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

  //RL-4: the inner query as a key projection — a predicate, a column, the lease and the token —
  //run on a pipeline of its own so that its report is its own (DG-2).
  private KeySet Project(RelationStepPlan step, QueryRequestPlan plan, CatalogLease lease,
      CancellationToken cancellation, List<QueryReport> inner, List<QueryReport> published) {
    var projection = new KeyProjection(step.Inner, step.FarColumn, lease, cancellation);
    var keys = new KeySet(step.Step.RelationName, plan.Options.KeySetCapBytes);
    var pipeline = new QueryPipeline(_data, _indexes, _pages, step.FarCollection, OrderSource.None,
      "a key projection has no order (RL-4)", null, 0, long.MaxValue, long.MaxValue, projection.Cancellation);
    pipeline.Describe(projection.Predicate);
    Guarded(pipeline, published, inner, () => {
      pipeline.Project(projection.Predicate, projection.Column, keys);
      return keys;
    });
    return keys;
  }

  //The plan with the projected sets in place of the placeholders it was made with, and the
  //planner's filter rule applied again over the bound conjuncts — a set of string keys makes a
  //seek that was exact over a placeholder inexact — and the In strategy chosen (RL-3b).
  private QueryPlan Bind(QueryRequestPlan plan, Dictionary<KeySet, KeySet> projected) {
    QueryPredicate BindPredicate(QueryPredicate predicate) {
      return predicate.Constants is KeySet placeholder && projected.TryGetValue(placeholder, out var keys)
        ? predicate with { Constants = keys }
        : predicate;
    }

    var conjuncts = plan.Rewritten.Conjuncts.Select(BindPredicate).ToImmutableArray();
    var path = plan.Access.Path;
    if (path is IndexSeekPath { Keys: { IsProjected: false } placeholder } seek
        && projected.TryGetValue(placeholder, out var keys)) {
      path = ChooseInStrategy(plan.Options, seek, keys);
    }
    return new QueryPlan(plan.CollectionName, path, QueryPlanner.Filters(path, conjuncts), plan.Access.Residual);
  }

  //RL-3b and Q-13: seeks below the crossover, one membership pass above it. The estimated probes
  //are the distinct keys times the height of the tree — one descent per key, and no page cache
  //to make the later ones cheap — against the data pages of the outer collection, which is what
  //a pass reads once. Both figures go into the path's own line, so the crossover can be measured.
  private AccessPath ChooseInStrategy(QueryOptions options, IndexSeekPath seek, KeySet keys) {
    var bound = new IndexSeekPath(seek.CollectionName, seek.ColumnName, seek.Predicate with { Constants = keys },
      seek.IsUnique);
    var index = _indexes.Find(seek.CollectionName, seek.ColumnName)
      ?? throw new InvalidOperationException(
        $"The plan seeks {seek.CollectionName}.{seek.ColumnName} through an index that no longer exists.");
    var height = index.Tree.Height();
    var pages = DataPages(seek.CollectionName);
    var probes = (long)keys.Count * height;
    var estimate = $"{keys.Count} keys × height {height} = {probes} probes against {pages} pages, " +
      $"crossover {options.MembershipCrossover}";
    var pass = options.InStrategy switch {
      InStrategy.Seeks => false,
      InStrategy.MembershipPass => true,
      _ => probes > options.MembershipCrossover * pages
    };
    if (!pass) {
      return bound;
    }
    var reason = options.InStrategy == InStrategy.MembershipPass
      ? $"forced by {nameof(QueryOptions.InStrategy)}; {estimate}"
      : estimate;
    return new MembershipPassPath(seek.CollectionName, seek.ColumnName, bound.Predicate, keys, reason);
  }

  //The pages holding the collection's records, as the free-space structure records them.
  private long DataPages(string collectionName) {
    return Math.Max(1, _freeSpace.GetEntries(collectionName).Count(entry => entry.CanHoldRecords));
  }

  //DG-3a and NF-4a: whatever the outcome, the pipeline's report is finalised once and published,
  //and a cancellation leaves as a distinct outcome carrying it rather than as a short page.
  private static T Guarded<T>(QueryPipeline pipeline, List<QueryReport> published, List<QueryReport> inner,
      Func<T> work) {
    try {
      var result = work();
      Publish(pipeline.Finalise(QueryOutcome.Completed), published, inner);
      return result;
    } catch (OperationCanceledException cancelled) {
      var report = pipeline.Finalise(QueryOutcome.Cancelled);
      Publish(report, published, inner);
      throw new QueryCancelledException(report, cancelled.CancellationToken, cancelled);
    } catch (Exception) {
      Publish(pipeline.Finalise(QueryOutcome.Failed), published, inner);
      throw;
    }
  }

  private static void Publish(QueryReport report, List<QueryReport> published, List<QueryReport> inner) {
    inner?.Add(report);
    published?.Add(report);
  }
}
