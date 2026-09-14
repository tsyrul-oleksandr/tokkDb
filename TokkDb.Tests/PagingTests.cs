using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Pages.Query;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

//Steps 3.1 and 3.2: a walk that can stop (PG-1, PG-2, PG-4, PG-5, DG-5, Q-7) and the paging
//figures (PG-3, PG-3a, PG-6, DG-1, DG-3a). Every row of the cost table of PG-3a that can be
//reached is a test, and the one that cannot is asserted unreachable.
[Collection(LargeEventsCollection.Name)]
public class PagingTests {
  private readonly LargeEventsFixture _events;
  private readonly ITestOutputHelper _output;

  public PagingTests(LargeEventsFixture events, ITestOutputHelper output) {
    _events = events;
    _output = output;
  }

  private int Lviv => _events.Events.Count(record => record.City == "Lviv");

  // =====================================================================
  // 3.1 — a walk that can stop.
  // =====================================================================

  //PG-1: the walk is lazy, so a Take stops the reading with the page it is on.
  [Fact]
  public void TakeTwentyOverAHundredThousandRecordsWithNoPredicateReadsThePagesOfTheFirstTwentyAndNoMore() {
    var result = _events.Entities.Query().Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal("full scan of Event (no predicate)", result.Report.AccessPath);
    Assert.Equal(1, result.Report.PagesRead);
    Assert.Equal(20, result.Report.RecordsExamined);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(20, result.Report.RecordsReturned);
    Assert.True(result.Report.IsStreaming);
  }

  //PG-2: a skipped record costs the fields the predicate and the order name, and no document.
  [Fact]
  public void SkipAThousandTakeTwentyMaterialisesTwenty() {
    var result = _events.Entities.Query().OrderBy("Date").Skip(1_000).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(OrderSource.IndexWalk, result.Report.OrderSource);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(1_000, result.Report.RecordsSkipped);
    Assert.Equal(20, result.Report.RecordsReturned);
    Assert.Equal(1_020, result.Report.RecordsExamined);
  }

  //DG-5: what Run hands back is a list and a closed report, and the event carried that report
  //before Run returned.
  [Fact]
  public void RunHandsBackAFinishedReportRatherThanALazyEnumerable() {
    var reports = new List<QueryReport>();
    _events.Db.Queries.QueryExecuted += reports.Add;
    try {
      var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv")).Take(5).Run();

      Assert.IsType<List<DbRecord<Event>>>(result.Records);
      Assert.Same(result.Report, Assert.Single(reports));
      Assert.True(result.Report.Elapsed > TimeSpan.Zero);
      Assert.Equal(5, result.Report.RecordsReturned);
      Assert.Equal(5, result.Report.DocumentsMaterialised);
      //Reading the records again changes nothing: the figures were final when the event saw them.
      Assert.Equal(5, result.Records.Count);
      Assert.Equal(5, result.Records.Select(record => record.Value.City).Count(city => city == "Lviv"));
      Assert.Same(result.Report, reports[0]);
    } finally {
      _events.Db.Queries.QueryExecuted -= reports.Add;
    }
  }

  //PG-4: Take without an order is any N matching records, and the guarantee is the one that can
  //be asserted — every record satisfies the predicate and the count is right. Skip without an
  //order does not build.
  [Fact]
  public void TakeFiveWithNoOrderReturnsFiveMatchingRecordsAndSkipFiveWithNoOrderDoesNotBuild() {
    var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv")).Take(5).Run();

    Assert.Equal(5, result.Records.Count);
    Assert.All(result.Records, record => Assert.Equal("Lviv", record.Value.City));
    Assert.Equal(5, result.Report.RecordsMatched);
    var refused = Assert.Throws<QueryRequestRefusedException>(() => _events.Entities.Query().Skip(5).Build());
    Assert.Equal(QueryRequestRefusal.SkipWithoutOrder, refused.Refusal);
  }

  // =====================================================================
  // 3.2 — the paging figures.
  // =====================================================================

  //PG-3: the cost of Skip grows with the offset, and the report carries it.
  [Fact]
  public void SkipTenThousandTakeTwentyReportsTenThousandSkipped() {
    var result = _events.Entities.Query().OrderBy("Date").Skip(10_000).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(10_000, result.Report.RecordsSkipped);
    Assert.Equal(10_020, result.Report.RecordsExamined);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Contains("10000 skipped, 20 returned", result.Report.ToString());
  }

  //PG-3a, row one: an index range chosen by the predicate, with the order from that same walk,
  //examines s + n matching records and materialises n.
  [Fact]
  public void CostTable_AnIndexRangeWithTheOrderFromItsWalkExaminesSkipPlusTakeMatchingRecords() {
    var result = _events.Entities.Query()
      .Where(CompareDate("Date", ComparisonOperator.GreaterOrEqual, LargeEventsFixture.FirstDate.AddDays(5)))
      .OrderBy("Date").Skip(100).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.StartsWith("index range on Event.Date", result.Report.AccessPath);
    Assert.Equal(OrderSource.IndexWalk, result.Report.OrderSource);
    Assert.Equal(120, result.Report.RecordsExamined);
    Assert.Equal(100, result.Report.RecordsSkipped);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(0, result.Report.RecordsRetained);
  }

  //PG-3a, row two, the one that misleads: an ordered walk chosen for the order examines index
  //entries until s + n match the predicate — about forty entries per kept record for one city in
  //forty, and the whole index for a city with no records at all.
  [Fact]
  public void CostTable_AnOrderedWalkChosenForTheOrderExaminesEntriesUntilThePageMatches() {
    var walk = new QueryOptions { PathChoice = PathChoice.OrderedWalk };
    var lviv = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Date").Skip(100).Take(20).WithOptions(walk).Run();
    var nowhere = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Nowhere"))
      .OrderBy("Date").Skip(100).Take(20).WithOptions(walk).Run();

    _output.WriteLine(lviv.Report.ToString());
    _output.WriteLine(nowhere.Report.ToString());
    Assert.Equal("ordered walk of the index on Event.Date", lviv.Report.AccessPath);
    Assert.Equal(120, lviv.Report.RecordsMatched);
    Assert.True(lviv.Report.RecordsExamined > 120 * 20, $"{lviv.Report.RecordsExamined} entries for 120 matches");
    Assert.Equal(20, lviv.Report.DocumentsMaterialised);
    Assert.Equal(0, lviv.Report.RecordsRetained);
    Assert.Equal(LargeEventsFixture.Count, nowhere.Report.RecordsExamined);
    Assert.Equal(0, nowhere.Report.RecordsMatched);
    Assert.Empty(nowhere.Records);
  }

  //PG-3a, row three: an index seek with a bounded heap examines every matching record and holds
  //s + n of them.
  [Fact]
  public void CostTable_AnIndexSeekWithABoundedHeapExaminesEveryMatchingRecord() {
    var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Date").Skip(100).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal("index seek on Event.City", result.Report.AccessPath);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.Equal(Lviv, result.Report.RecordsExamined);
    Assert.Equal(120, result.Report.RecordsRetained);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.False(result.Report.IsStreaming);
  }

  //PG-3a, row four: a full scan has no key order, so no plan takes an order from one. Asserted on
  //the rule and on the planner: where the predicate scans and the order has an index, the plan
  //walks the index instead; where the order has no index, the plan sorts.
  [Fact]
  public void CostTable_AFullScanWithTheOrderFromAWalkIsUnreachable() {
    Assert.Null(OrderRules.KeyOrderOf(new FullScanPath(LargeEventsFixture.Collection, "no predicate")));
    Assert.False(OrderRules.WalkSatisfies(new FullScanPath(LargeEventsFixture.Collection, "no predicate"), [new OrderColumn("Date")]));

    var plans = new[] {
      _events.Entities.Query().Where(Compare("Cost", ComparisonOperator.Greater, 10)).OrderBy("Cost").Take(20).Explain(),
      _events.Entities.Query().Where(Compare("Cost", ComparisonOperator.Greater, 10)).OrderBy("Date").Take(20).Explain(),
      _events.Entities.Query().OrderBy("Date").Explain(),
      _events.Entities.Query().Where(Compare("Cost", ComparisonOperator.Greater, 10)).OrderBy("Date").Explain(),
      _events.Entities.Query().Where(Compare("Cost", ComparisonOperator.Greater, 10)).OrderBy("Date").Take(20)
        .WithOptions(new QueryOptions { PathChoice = PathChoice.PredicatePath }).Explain()
    };

    foreach (var plan in plans) {
      _output.WriteLine(plan.ToString());
      if (plan.Access.Path is FullScanPath) {
        Assert.NotEqual(OrderSource.IndexWalk, plan.OrderSource);
      } else {
        Assert.IsType<OrderedIndexWalkPath>(plan.Access.Path);
      }
    }
    Assert.Contains(plans, plan => plan.Access.Path is FullScanPath);
    Assert.Contains(plans, plan => plan.Access.Path is OrderedIndexWalkPath);
  }

  //PG-3a, row five: a full scan with a bounded heap examines every live record.
  [Fact]
  public void CostTable_AFullScanWithABoundedHeapExaminesEveryLiveRecord() {
    var result = _events.Entities.Query().Where(Compare("Cost", ComparisonOperator.GreaterOrEqual, 500))
      .OrderBy("Cost").Skip(100).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.StartsWith("full scan of Event", result.Report.AccessPath);
    Assert.Equal(LargeEventsFixture.Count, result.Report.RecordsExamined);
    Assert.Equal(120, result.Report.RecordsRetained);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
  }

  //PG-3a, row six: a complete sort holds every matching record, and with no Take returns them all.
  [Fact]
  public void CostTable_ACompleteSortHoldsEveryMatchingRecord() {
    var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(OrderSource.CompleteSort, result.Report.OrderSource);
    Assert.Equal(Lviv, result.Report.RecordsExamined);
    Assert.Equal(Lviv, result.Report.RecordsRetained);
    Assert.Equal(Lviv, result.Report.DocumentsMaterialised);
  }

  // =====================================================================
  // A total, only when asked for (PG-6).
  // =====================================================================

  [Fact]
  public void APagedQueryThatDidNotAskForATotalDoesNotComputeOneAndOneThatDidShowsTheExtraPass() {
    var query = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv")).Take(20);

    var page = query.Run();
    var counted = query.IncludeTotal().Run();

    _output.WriteLine(page.Report.ToString());
    _output.WriteLine(counted.Report.ToString());
    Assert.Null(page.Report.TotalCount);
    Assert.Null(page.Report.TotalCountPass);
    Assert.Equal(20, page.Report.RecordsExamined);
    Assert.Equal(Lviv, counted.Report.TotalCount);
    Assert.Equal(Lviv, counted.Report.TotalCountPass.RecordsExamined);
    Assert.True(counted.Report.TotalCountPass.PagesRead > page.Report.PagesRead);
    //The page itself cost what it cost without the total; the pass is its own figure.
    Assert.Equal(page.Report.PagesRead, counted.Report.PagesRead);
    Assert.Equal(20, counted.Report.RecordsExamined);
    Assert.Contains($"total {Lviv} (counting pass:", counted.Report.ToString());
  }

  //Where the walk examined everything anyway, the total is known and the extra pass is nothing.
  [Fact]
  public void ATotalOverAnOrderingStageCostsNoExtraPass() {
    var counted = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Take(20).IncludeTotal().Run();

    Assert.Equal(Lviv, counted.Report.TotalCount);
    Assert.Equal(new CountingPass(0, 0), counted.Report.TotalCountPass);
  }

  // =====================================================================
  // A failed or cancelled execution raises the event once (DG-3a).
  // =====================================================================

  //A residual that does something on the nth record it sees. It is evaluated per record against
  //the page, so what it does happens inside the walk.
  private sealed class OnNthRecord : IExpression {
    private readonly int _nth;
    private readonly Action _action;
    private int _seen;

    public OnNthRecord(int nth, Action action) {
      _nth = nth;
      _action = action;
    }

    public IExpression Parent { get; set; }

    public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
      if (++_seen == _nth) {
        _action();
      }
      return new BooleanDocumentValue(true);
    }
  }

  [Fact]
  public void ACancelledQueryRaisesExactlyOneEventCarryingThePartialFigures() {
    var reports = new List<QueryReport>();
    _events.Db.Queries.QueryExecuted += reports.Add;
    try {
      using var cancellation = new CancellationTokenSource();
      var query = _events.Entities.Query()
        .Where(new OnNthRecord(500, cancellation.Cancel))
        .OrderBy("Cost").Take(10);

      var cancelled = Assert.Throws<QueryCancelledException>(() => query.Run(cancellation.Token));

      _output.WriteLine(cancelled.Message);
      _output.WriteLine(cancelled.Report.ToString());
      var report = Assert.Single(reports);
      Assert.Same(cancelled.Report, report);
      Assert.Equal(QueryOutcome.Cancelled, report.Outcome);
      Assert.Equal(500, report.RecordsExamined);
      Assert.Equal(0, report.DocumentsMaterialised);
      Assert.Contains("cancelled with the figures so far", report.ToString());
    } finally {
      _events.Db.Queries.QueryExecuted -= reports.Add;
    }
  }

  [Fact]
  public void AQueryThatThrewRaisesExactlyOneEventCarryingThePartialFigures() {
    var reports = new List<QueryReport>();
    _events.Db.Queries.QueryExecuted += reports.Add;
    try {
      var query = _events.Entities.Query()
        .Where(new OnNthRecord(300, () => throw new InvalidOperationException("the record is cursed")))
        .Take(1_000);

      var failed = Assert.Throws<InvalidOperationException>(() => query.Run());

      Assert.Equal("the record is cursed", failed.Message);
      var report = Assert.Single(reports);
      Assert.Equal(QueryOutcome.Failed, report.Outcome);
      Assert.Equal(300, report.RecordsExamined);
      Assert.Equal(299, report.RecordsMatched);
      Assert.Contains("failed with the figures so far", report.ToString());
    } finally {
      _events.Db.Queries.QueryExecuted -= reports.Add;
    }
  }

  //DG-1: every new figure appears in ToString.
  [Fact]
  public void TheReportLineCarriesEveryFigure() {
    var line = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Skip(3).Take(4).IncludeTotal().Run().Report.ToString();

    _output.WriteLine(line);
    Assert.Contains("order from a bounded heap that retained 7 records", line);
    Assert.Contains("3 skipped, 4 returned", line);
    Assert.Contains("not streaming", line);
    Assert.Contains($"total {Lviv}", line);
    Assert.Contains("index probes", line);
  }
}
