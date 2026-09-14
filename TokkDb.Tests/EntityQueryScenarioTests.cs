using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

//Step 5.1: the scenarios of §5 that define "done", each asserting what it returns and what it
//cost — and NF-2's rule that no scenario materialises the collection. S-5 to S-7 and S-10 live
//in RelationQueryTests with the relation fixture; N-1 to N-5, N-7, N-7a and N-9 in
//EntityQueryTests, where the refusals were first pinned.
[Collection(LargeEventsCollection.Name)]
public class EntityQueryScenarioTests {
  private readonly LargeEventsFixture _events;
  private readonly ITestOutputHelper _output;

  public EntityQueryScenarioTests(LargeEventsFixture events, ITestOutputHelper output) {
    _events = events;
    _output = output;
  }

  private void Cost(string scenario, QueryReport report) {
    _output.WriteLine($"{scenario}: {report}");
    Assert.NotEqual(LargeEventsFixture.Count, report.DocumentsMaterialised);
  }

  private IEnumerable<(Ulid Id, Event Event)> Ordered(Func<Event, IComparable> key, bool descending = false) {
    return descending
      ? _events.Records.OrderByDescending(pair => key(pair.Event)).ThenByDescending(pair => pair.Id, IdentityOrder.Bytes)
      : _events.Records.OrderBy(pair => key(pair.Event)).ThenBy(pair => pair.Id, IdentityOrder.Bytes);
  }

  //S-1. Ordered page, free: with no predicate, and with a predicate on the ordered column.
  [Fact]
  public void S1_AnOrderedPageOverAnIndexedColumnIsFree() {
    var free = _events.Entities.Query().OrderBy("Date").Skip(20).Take(20).Run();
    var withPredicate = _events.Entities.Query()
      .Where(CompareDate("Date", ComparisonOperator.GreaterOrEqual, LargeEventsFixture.FirstDate.AddDays(3)))
      .OrderBy("Date").Skip(20).Take(20).Run();

    Cost("S-1 no predicate", free.Report);
    Cost("S-1 predicate on Date", withPredicate.Report);
    foreach (var result in new[] { free, withPredicate }) {
      Assert.Equal(OrderSource.IndexWalk, result.Report.OrderSource);
      Assert.Equal(0, result.Report.RecordsRetained);
      Assert.Equal(20, result.Report.DocumentsMaterialised);
      Assert.Equal(40, result.Report.RecordsExamined);
    }
    Assert.Equal(Ordered(record => record.Date).Skip(20).Take(20).Select(pair => pair.Id), free.Records.Select(record => record.RecordId));
    Assert.Equal(Ordered(record => record.Date).Where(pair => pair.Event.Date >= LargeEventsFixture.FirstDate.AddDays(3)).Skip(20).Take(20).Select(pair => pair.Id),
      withPredicate.Records.Select(record => record.RecordId));
  }

  //S-2. Ordered page, bounded heap: the records are filtered first, the stage keeps Skip + Take.
  [Fact]
  public void S2_AnOrderedPageOverAnUnindexedColumnIsABoundedHeap() {
    var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Skip(20).Take(20).Run();

    Cost("S-2", result.Report);
    var lviv = _events.Records.Where(pair => pair.Event.City == "Lviv").ToList();
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.Equal(lviv.Count, result.Report.RecordsExamined);
    Assert.Equal(40, result.Report.RecordsRetained);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(lviv.OrderBy(pair => pair.Event.Cost).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Skip(20).Take(20).Select(pair => pair.Id),
      result.Records.Select(record => record.RecordId));
  }

  //S-3. Descending: the right order, produced by the stage, with the reason recorded (Q-3).
  [Fact]
  public void S3_DescendingIsProducedByTheStageWithTheReasonRecorded() {
    var query = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderByDescending("Date").Skip(20).Take(20);

    var plan = query.Explain();
    var result = query.Run();

    Cost("S-3", result.Report);
    Assert.Contains("forward only", plan.OrderReason);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.Equal(_events.Records.Where(pair => pair.Event.City == "Lviv").OrderByDescending(pair => pair.Event.Date)
        .ThenByDescending(pair => pair.Id, IdentityOrder.Bytes).Skip(20).Take(20).Select(pair => pair.Id),
      result.Records.Select(record => record.RecordId));
  }

  //S-4. Ties: ten thousand records share a date, and page five and page six neither repeat a
  //record nor skip one, because identity is the last key.
  [Fact]
  public void S4_PagesAcrossTenThousandTiesNeitherRepeatNorSkipARecord() {
    var pages = Enumerable.Range(0, 8)
      .Select(page => _events.Entities.Query().OrderBy("Date").Skip(page * 2_000).Take(2_000).Run())
      .ToList();

    Cost("S-4 page 5", pages[4].Report);
    var seen = pages.SelectMany(page => page.Records.Select(record => record.RecordId)).ToList();
    Assert.Equal(16_000, seen.Count);
    Assert.Equal(16_000, seen.Distinct().Count());
    Assert.Equal(Ordered(record => record.Date).Take(16_000).Select(pair => pair.Id), seen);
    Assert.All(pages[4].Records, record => Assert.Equal(LargeEventsFixture.FirstDate, record.Value.Date));
    Assert.All(pages[5].Records, record => Assert.Equal(LargeEventsFixture.FirstDate.AddDays(1), record.Value.Date));
  }

  //S-8. Everything at once: a relation filter, two order columns, a skip and a take, over a
  //collection of 100 000 records, materialising exactly one page.
  [Fact]
  public void S8_EverythingAtOnceMaterialisesExactlyOnePage() {
    const string venues = "Venue";
    if (!_events.Db.Collections.Any(collection => collection.Name == venues)) {
      _events.Db.CreateCollection(venues, [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true), new ColumnDescriptor("Region", ValueTypeEnum.String)]);
      _events.Db.CreateRelation("EventVenue", LargeEventsFixture.Collection, "City", venues, "Name");
      var entities = _events.Db.Entities<Venue>(venues);
      _events.Db.InTransaction(() => {
        foreach (var (city, i) in LargeEventsFixture.Cities.Select((city, i) => (city, i))) {
          entities.Insert(new Venue { Name = city, Region = i % 4 == 0 ? "West" : "East" });
        }
      });
    }
    var query = _events.Entities.Query()
      .WhereRelated("EventVenue", RelationQuantifier.Any, CompareText("Region", ComparisonOperator.Equal, "West"))
      .OrderBy("Date").ThenByDescending("Cost").Skip(50).Take(20);

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    Cost("S-8", result.Report);
    var west = LargeEventsFixture.Cities.Where((_, i) => i % 4 == 0).ToHashSet();
    var expected = _events.Records.Where(pair => west.Contains(pair.Event.City))
      .OrderBy(pair => pair.Event.Date).ThenByDescending(pair => pair.Event.Cost).ThenByDescending(pair => pair.Id, IdentityOrder.Bytes)
      .Skip(50).Take(20);
    Assert.Equal(expected.Select(pair => pair.Id), result.Records.Select(record => record.RecordId));
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(70, result.Report.RecordsRetained);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    var inner = Assert.Single(result.Report.InnerReports);
    Assert.Equal(10, inner.DistinctKeys);
    Assert.Equal(0, inner.DocumentsMaterialised);
  }

  public sealed class Venue {
    public string Name { get; set; }
    public string Region { get; set; }
  }

  //S-9. Nulls: a collection where some records have no value in the order column and some have
  //no value in the join column. The nulls order first ascending and last descending, and the
  //records with a null join value are absent from Any and present in None.
  [Fact]
  public void S9_NullsOrderFirstAscendingAndLastDescendingAndJoinToNothing() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection("Room", [new ColumnDescriptor("Number", ValueTypeEnum.Int, unique: true)]);
    db.CreateCollection("Booking", [new ColumnDescriptor("Id", ValueTypeEnum.Int), new ColumnDescriptor("Room", ValueTypeEnum.Int), new ColumnDescriptor("Guests", ValueTypeEnum.Int)]);
    db.CreateRelation("BookingRoom", "Booking", "Room", "Room", "Number");
    db.CreateIndex("Booking", "Guests");
    var rooms = db.Entities<Room>("Room");
    var bookings = db.Entities<Booking>("Booking");
    var ids = new List<(Ulid Id, Booking Value)>();
    db.InTransaction(() => {
      for (var number = 1; number <= 5; number++) {
        rooms.Insert(new Room { Number = number });
      }
      for (var i = 0; i < 60; i++) {
        var booking = new Booking { Id = i, Room = i % 4 == 0 ? null : i % 5 + 1, Guests = i % 3 == 0 ? null : i % 7 };
        ids.Add((bookings.Insert(booking), booking));
      }
    });

    var ascending = bookings.Query().OrderBy("Guests").Take(60).Run();
    var descending = bookings.Query().OrderByDescending("Guests").Take(60).Run();
    var any = bookings.Query().WhereRelated("BookingRoom", RelationQuantifier.Any).Run();
    var none = bookings.Query().WhereRelated("BookingRoom", RelationQuantifier.None).Run();

    Cost("S-9 ascending", ascending.Report);
    Cost("S-9 any", any.Report);
    Assert.Equal(OrderSource.IndexWalk, ascending.Report.OrderSource);
    Assert.Equal(ids.OrderBy(pair => pair.Value.Guests ?? int.MinValue).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Select(pair => pair.Id),
      ascending.Records.Select(record => record.RecordId));
    Assert.Equal(ids.OrderByDescending(pair => pair.Value.Guests ?? int.MinValue).ThenByDescending(pair => pair.Id, IdentityOrder.Bytes).Select(pair => pair.Id),
      descending.Records.Select(record => record.RecordId));
    Assert.Equal(ids.Where(pair => pair.Value.Room is not null).Select(pair => pair.Value.Id).Order(), any.Records.Select(record => record.Value.Id).Order());
    Assert.Equal(ids.Where(pair => pair.Value.Room is null).Select(pair => pair.Value.Id).Order(), none.Records.Select(record => record.Value.Id).Order());
  }

  public sealed class Room {
    public int Number { get; set; }
  }

  public sealed class Booking {
    public int Id { get; set; }
    public int? Room { get; set; }
    public int? Guests { get; set; }
  }

  //N-8. A page starting past the last record returns nothing, reports it, and is not an error.
  [Fact]
  public void N8_APageBeyondTheEndIsEmptyAndReported() {
    var result = _events.Entities.Query().Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Date").Skip(1_000_000).Take(20).Run();

    Cost("N-8", result.Report);
    Assert.Empty(result.Records);
    Assert.Equal(_events.Events.Count(record => record.City == "Lviv"), result.Report.RecordsSkipped);
    Assert.Equal(0, result.Report.RecordsReturned);
  }

  //N-11. Arithmetic at the edges: int.MaxValue twice builds, plans and returns an empty page.
  [Fact]
  public void N11_TheWindowAtTheEdgeOfTheArithmeticIsAnEmptyPage() {
    var query = _events.Entities.Query().OrderBy("Date").Skip(int.MaxValue).Take(int.MaxValue);

    var plan = query.Explain();
    var result = query.Run();

    Cost("N-11", result.Report);
    Assert.Equal(2L * int.MaxValue, plan.Request.End);
    Assert.Empty(result.Records);
    Assert.Equal(LargeEventsFixture.Count, result.Report.RecordsSkipped);
  }

  // =====================================================================
  // N-10. Cancellation (NF-4, NF-4a).
  // =====================================================================

  private sealed class CancelAfter : IExpression {
    private readonly int _records;
    private readonly CancellationTokenSource _source;
    private int _seen;

    public CancelAfter(int records, CancellationTokenSource source) {
      _records = records;
      _source = source;
    }

    public IExpression Parent { get; set; }

    public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
      if (++_seen == _records) {
        _source.Cancel();
      }
      return new BooleanDocumentValue(true);
    }
  }

  //A long scan and a complete sort each stop within a bounded number of records of the
  //cancellation, and end as a cancelled outcome carrying the partial report — never as a short page.
  [Theory]
  [InlineData("scan")]
  [InlineData("sort")]
  public void N10_ACancelledScanOrSortStopsWithinABoundedNumberOfRecordsAndCarriesItsPartialReport(string shape) {
    using var source = new CancellationTokenSource();
    var query = _events.Entities.Query().Where(new CancelAfter(2_000, source));
    query = shape == "scan" ? query.Take(5_000) : query.OrderBy("Cost");

    var cancelled = Assert.Throws<QueryCancelledException>(() => query.Run(source.Token));

    Cost($"N-10 {shape}", cancelled.Report);
    Assert.Equal(QueryOutcome.Cancelled, cancelled.Report.Outcome);
    Assert.Equal(2_000, cancelled.Report.RecordsExamined);
    Assert.True(cancelled.Report.PagesRead > 0);
    //A streaming walk had materialised what it returned before the cancellation reached it; a
    //sort had materialised nothing, because nothing is materialised until the order is known.
    Assert.Equal(shape == "scan" ? 2_000 : 0, cancelled.Report.DocumentsMaterialised);
    Assert.Equal(source.Token, cancelled.CancellationToken);
    Assert.Contains("cancelled", cancelled.Message);
  }

  //A cancelled semi-join: the token reaches the inner query, which stops, and the outer ends
  //cancelled with the inner's partial report nested in its own.
  [Fact]
  public void N10_CancellingAnOuterQueryCancelsItsInnerQuery() {
    using var file = new TempDatabaseFile();
    using var data = new RelationFixture();
    using var source = new CancellationTokenSource();
    var reports = new List<QueryReport>();
    data.Db.Queries.QueryExecuted += reports.Add;
    var query = data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, new CancelAfter(300, source));

    var cancelled = Assert.Throws<QueryCancelledException>(() => query.Run(source.Token));

    _output.WriteLine(cancelled.Report.ToString());
    Assert.Equal(QueryOutcome.Cancelled, cancelled.Report.Outcome);
    Assert.Equal(0, cancelled.Report.RecordsExamined);
    var inner = Assert.Single(cancelled.Report.InnerReports);
    Assert.Equal(QueryOutcome.Cancelled, inner.Outcome);
    Assert.Equal(300, inner.RecordsExamined);
    Assert.IsType<QueryCancelledException>(cancelled.InnerException);
    Assert.Equal(2, reports.Count);
    Assert.Same(inner, reports[0]);
    Assert.Same(cancelled.Report, reports[1]);
  }

  //NF-4a: a cancellation is never an ordinary result.
  [Fact]
  public void N10_ACancelledQueryCannotBeMistakenForAFinishedOne() {
    using var source = new CancellationTokenSource();
    source.Cancel();

    var cancelled = Assert.Throws<QueryCancelledException>(() => _events.Entities.Query().Take(12).Run(source.Token));

    Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
    Assert.Equal(0, cancelled.Report.RecordsReturned);
    Assert.Equal(QueryOutcome.Cancelled, cancelled.Report.Outcome);
  }

  //NF-1. Take(20) over an ordered indexed column reads a number of pages bounded by the page
  //window, not by the collection.
  [Fact]
  public void NF1_ATakeOverAnOrderedIndexedColumnReadsPagesBoundedByTheWindow() {
    var result = _events.Entities.Query().OrderBy("Date").Take(20).Run();

    Cost("NF-1", result.Report);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.True(result.Report.PagesRead <= 20 + 8, $"{result.Report.PagesRead} pages for a page of 20");
  }
}
