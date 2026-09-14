using System.Reflection;
using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Pages.Records;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

public sealed class Meeting {
  public int Id { get; set; }
  public string City { get; set; }
}

public sealed class Payment {
  public int Id { get; set; }
  public int? ConferenceId { get; set; }
  public int Amount { get; set; }
}

public sealed class Chore {
  public int Id { get; set; }
  public int? ParentId { get; set; }
  public string Title { get; set; }
}

//Two thousand meetings, five of them in Lviv; four thousand payments, two per meeting for the
//first 1 990 meetings and none for the last ten, every five-hundredth with no meeting at all; and
//thirty chores in a tree three wide. Built once per test class.
public sealed class RelationFixture : IDisposable {
  public const string Meetings = nameof(Meeting);
  public const string Payments = nameof(Payment);
  public const string Chores = nameof(Chore);
  public const string PaymentMeeting = "ExpenseConference";
  public const string ChoreParent = "TaskParent";
  public const int MeetingCount = 2_000;
  public const int PaymentCount = 4_000;
  public const int MeetingsWithoutPayments = 10;

  public RelationFixture() : this(indexPaymentMeeting: true) { }

  //A class fixture has one public constructor; the tests that need the near column unindexed
  //build their own through this one.
  internal RelationFixture(bool indexPaymentMeeting) {
    File = new TempDatabaseFile();
    Db = new TokkDbConnection(File.Path);
    Db.Load();
    Db.CreateCollection(Meetings, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int, unique: true),
      new ColumnDescriptor("City", ValueTypeEnum.String)
    ]);
    Db.CreateCollection(Payments, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("ConferenceId", ValueTypeEnum.Int),
      new ColumnDescriptor("Amount", ValueTypeEnum.Int)
    ]);
    Db.CreateCollection(Chores, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int, unique: true),
      new ColumnDescriptor("ParentId", ValueTypeEnum.Int),
      new ColumnDescriptor("Title", ValueTypeEnum.String)
    ]);
    foreach (var collection in new[] { Meetings, Payments, Chores }) {
      Db.SetRetentionPolicy(collection, RetentionPolicy.None, dropHistory: true);
    }
    Db.CreateRelation(PaymentMeeting, Payments, "ConferenceId", Meetings, "Id");
    Db.CreateRelation(ChoreParent, Chores, "ParentId", Chores, "Id");
    if (indexPaymentMeeting) {
      Db.CreateIndex(Payments, "ConferenceId");
    }
    Db.InTransaction(() => {
      var meetings = Db.Entities<Meeting>(Meetings);
      for (var i = 1; i <= MeetingCount; i++) {
        var meeting = new Meeting { Id = i, City = i % 400 == 0 ? "Lviv" : $"City-{i % 40}" };
        MeetingRecords.Add((meetings.Insert(meeting), meeting));
      }
      var payments = Db.Entities<Payment>(Payments);
      for (var i = 0; i < PaymentCount; i++) {
        var payment = new Payment {
          Id = i,
          ConferenceId = i % 500 == 0 ? null : i % (MeetingCount - MeetingsWithoutPayments) + 1,
          Amount = (i * 37) % 1000
        };
        PaymentRecords.Add((payments.Insert(payment), payment));
      }
      var chores = Db.Entities<Chore>(Chores);
      for (var i = 1; i <= 30; i++) {
        var chore = new Chore { Id = i, ParentId = i <= 3 ? null : i / 3, Title = i == 3 ? "urgent" : i == 10 ? "overdue" : $"chore {i}" };
        ChoreRecords.Add((chores.Insert(chore), chore));
      }
    });
  }

  public TempDatabaseFile File { get; }
  public TokkDbConnection Db { get; }
  public List<(Ulid Id, Meeting Value)> MeetingRecords { get; } = [];
  public List<(Ulid Id, Payment Value)> PaymentRecords { get; } = [];
  public List<(Ulid Id, Chore Value)> ChoreRecords { get; } = [];

  public DbEntities<Meeting> MeetingEntities => Db.Entities<Meeting>(Meetings);
  public DbEntities<Payment> PaymentEntities => Db.Entities<Payment>(Payments);
  public DbEntities<Chore> ChoreEntities => Db.Entities<Chore>(Chores);

  public HashSet<int> MeetingsIn(string city) {
    return MeetingRecords.Where(pair => pair.Value.City == city).Select(pair => pair.Value.Id).ToHashSet();
  }

  public void Dispose() {
    Db.Dispose();
    File.Dispose();
  }
}

//Steps 4.1 to 4.4: direction and refusals (RL-1, RL-2, RL-3, RL-7, RL-8, RL-12), the semi-join
//(RL-3a, RL-3b, RL-3c, RL-4, RL-6, RL-9, RL-10, RL-11), None and what it costs (RL-5, Q-8), and
//two reports rather than one (DG-2, DG-3, Q-10).
public class RelationQueryTests : IClassFixture<RelationFixture> {
  private readonly RelationFixture _data;
  private readonly ITestOutputHelper _output;

  public RelationQueryTests(RelationFixture data, ITestOutputHelper output) {
    _data = data;
    _output = output;
  }

  private static IEnumerable<int> Sorted(IEnumerable<DbRecord<Payment>> records) => records.Select(record => record.Value.Id).Order();
  private static IEnumerable<int> Sorted(IEnumerable<DbRecord<Meeting>> records) => records.Select(record => record.Value.Id).Order();

  private List<QueryReport> Subscribe() {
    var reports = new List<QueryReport>();
    _data.Db.Queries.QueryExecuted += reports.Add;
    return reports;
  }

  private void Unsubscribe(List<QueryReport> reports) {
    _data.Db.Queries.QueryExecuted -= reports.Add;
  }

  // =====================================================================
  // The semi-join (4.2).
  // =====================================================================

  //S-5. Expenses whose conference is in Lviv: one inner query over the meetings, an In on
  //Payment.ConferenceId executed as seeks, two reports, and the probe count reported.
  [Fact]
  public void S5_ARelationForwardWithASmallKeySetSeeksAndReportsTwoPlansAndItsProbes() {
    var reports = Subscribe();
    try {
      var result = _data.PaymentEntities.Query()
        .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "Lviv"))
        .Run();

      _output.WriteLine(result.Report.ToString());
      var lviv = _data.MeetingsIn("Lviv");
      var expected = _data.PaymentRecords.Where(pair => pair.Value.ConferenceId is { } id && lviv.Contains(id)).Select(pair => pair.Value.Id).Order();
      Assert.Equal(expected, Sorted(result.Records));
      Assert.Equal($"index seek on Payment.ConferenceId for {lviv.Count} keys projected across ExpenseConference", result.Report.AccessPath);
      Assert.Equal(lviv.Count, result.Report.IndexProbes);
      Assert.Equal(result.Records.Count, result.Report.DocumentsMaterialised);
      var inner = Assert.Single(result.Report.InnerReports);
      Assert.Equal("full scan of Meeting (no index on City)", inner.AccessPath);
      Assert.Equal(RelationFixture.MeetingCount, inner.RecordsExamined);
      Assert.Equal(lviv.Count, inner.RecordsMatched);
      Assert.Equal(lviv.Count, inner.DistinctKeys);
      Assert.Equal(0, inner.DocumentsMaterialised);
      //DG-3: both events, inner first.
      Assert.Equal(2, reports.Count);
      Assert.Same(inner, reports[0]);
      Assert.Same(result.Report, reports[1]);
    } finally {
      Unsubscribe(reports);
    }
  }

  //S-5a. The same where most conferences match: the In is one pass over the payments against the
  //set, reported as such, and returns the same records as the seeks would.
  [Fact]
  public void S5a_ARelationForwardWithALargeKeySetRunsAsOneMembershipPass() {
    var query = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.NotEqual, "Nowhere"));

    var plan = query.Explain();
    var pass = query.Run();
    var seeks = query.WithOptions(new QueryOptions { InStrategy = InStrategy.Seeks }).Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(pass.Report.ToString());
    _output.WriteLine(seeks.Report.ToString());
    Assert.Equal("index seek on Payment.ConferenceId for the keys projected across ExpenseConference", plan.Access.Path.Describe());
    Assert.StartsWith($"membership pass over Payment.ConferenceId against {RelationFixture.MeetingCount} keys", pass.Report.AccessPath);
    Assert.Contains("crossover 1", pass.Report.AccessPath);
    Assert.Equal(0, pass.Report.IndexProbes);
    Assert.Equal(RelationFixture.PaymentCount, pass.Report.RecordsExamined);
    Assert.StartsWith("index seek on Payment.ConferenceId", seeks.Report.AccessPath);
    Assert.Equal(RelationFixture.MeetingCount, seeks.Report.IndexProbes);
    Assert.Equal(Sorted(seeks.Records), Sorted(pass.Records));
    var expected = _data.PaymentRecords.Where(pair => pair.Value.ConferenceId is not null).Select(pair => pair.Value.Id).Order();
    Assert.Equal(expected, Sorted(pass.Records));
  }

  [Fact]
  public void BothStrategiesReturnTheSameRecordsForTheSameQuery() {
    var query = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "City-7"))
      .OrderBy("Amount").ThenBy("Id").Take(50);

    var seeks = query.WithOptions(new QueryOptions { InStrategy = InStrategy.Seeks }).Run();
    var pass = query.WithOptions(new QueryOptions { InStrategy = InStrategy.MembershipPass }).Run();

    _output.WriteLine(seeks.Report.ToString());
    _output.WriteLine(pass.Report.ToString());
    Assert.StartsWith("index seek", seeks.Report.AccessPath);
    Assert.StartsWith("membership pass", pass.Report.AccessPath);
    Assert.Contains("forced", pass.Report.AccessPath);
    Assert.Equal(seeks.Records.Select(record => record.RecordId), pass.Records.Select(record => record.RecordId));
    Assert.Equal(50, seeks.Records.Count);
  }

  //S-6. Conferences that have an expense over 500: one inner query over the payments, and the
  //outer path whatever the planner's own rule chooses for the rewritten conjunct — the unique
  //index on Meeting.Id, with no special case in the relation code (RL-9). The identity of a record
  //is not a column, so no relation targets the primary index; the unique index is the nearest
  //thing and the planner prefers it over an ordinary one by its existing rule.
  [Fact]
  public void S6_ARelationReverseReachesTheOuterRecordsByThePlannersOwnRule() {
    var query = _data.MeetingEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, Compare("Amount", ComparisonOperator.Greater, 500));

    var plan = query.Explain();
    var result = query.Run();
    var seeks = query.WithOptions(new QueryOptions { InStrategy = InStrategy.Seeks }).Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    var step = Assert.Single(plan.RelationSteps);
    Assert.Equal((RelationDirection.ToSource, "Id", RelationFixture.Payments, "ConferenceId"),
      (step.Direction, step.NearColumn, step.FarCollection, step.FarColumn));
    Assert.Equal("unique index seek on Meeting.Id for the keys projected across ExpenseConference", plan.Access.Path.Describe());
    var expected = _data.PaymentRecords.Where(pair => pair.Value.Amount > 500 && pair.Value.ConferenceId is not null)
      .Select(pair => pair.Value.ConferenceId.Value).Distinct().Order().ToList();
    Assert.Equal(expected, Sorted(result.Records));
    Assert.Equal(expected, Sorted(seeks.Records));
    Assert.StartsWith("unique index seek on Meeting.Id for", seeks.Report.AccessPath);
    Assert.Equal(expected.Count, seeks.Report.IndexProbes);
    Assert.Equal(expected.Count, result.Report.DocumentsMaterialised);
    var inner = Assert.Single(result.Report.InnerReports);
    Assert.Equal("full scan of Payment (no index on Amount)", inner.AccessPath);
    Assert.Equal(0, inner.DocumentsMaterialised);
  }

  //RL-3a: a repeated far value is one key, so it seeks once and returns its record once.
  [Fact]
  public void TheProjectedKeysAreDeduplicated() {
    var result = _data.MeetingEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, Compare("Amount", ComparisonOperator.Greater, 100))
      .WithOptions(new QueryOptions { InStrategy = InStrategy.Seeks })
      .Run();

    var inner = Assert.Single(result.Report.InnerReports);
    var matching = _data.PaymentRecords.Where(pair => pair.Value.Amount > 100 && pair.Value.ConferenceId is not null).ToList();
    var distinct = matching.Select(pair => pair.Value.ConferenceId).Distinct().Count();
    _output.WriteLine($"{inner.RecordsMatched} matched, {inner.DistinctKeys} distinct");
    Assert.True(distinct < matching.Count, "the data has repeats to deduplicate");
    Assert.Equal(distinct, inner.DistinctKeys);
    Assert.Equal(distinct, result.Report.IndexProbes);
    Assert.Equal(distinct, result.Records.Count);
    Assert.Equal(distinct, result.Records.Select(record => record.RecordId).Distinct().Count());
  }

  //RL-4: an inner query materialises nothing however many records it matches, and the type that
  //carries it has exactly four members and none for an order, a page or a result.
  [Fact]
  public void TheInnerQueryIsAKeyProjectionWithNoOrderPageOrResult() {
    var result = _data.MeetingEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, Compare("Amount", ComparisonOperator.GreaterOrEqual, 0))
      .Run();
    var inner = Assert.Single(result.Report.InnerReports);
    Assert.True(inner.RecordsMatched > 3_000);
    Assert.Equal(0, inner.DocumentsMaterialised);
    Assert.Equal(OrderSource.None, inner.OrderSource);

    var projection = typeof(QueryPlan).Assembly.GetType("TokkDb.Pages.Query.KeyProjection");
    Assert.NotNull(projection);
    var members = projection.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order().ToList();
    Assert.Equal(["Cancellation", "Column", "Lease", "Predicate"], members);
    foreach (var member in projection.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
      foreach (var forbidden in new[] { "Order", "Skip", "Take", "Page", "Result", "Match", "Document" }) {
        Assert.DoesNotContain(forbidden, member.Name);
      }
    }
  }

  //RL-3a's invariant, as a check on every plan: the old entry point handed a residual carrying a
  //relation step is refused before any record is read, wherever in the tree the step sits.
  [Fact]
  public void APlanReachingTheExecutorWithARelationStepIsRefusedByTheInvariant() {
    var direct = new NormalizedQuery([], Step(RelationFixture.PaymentMeeting));
    var buried = new NormalizedQuery([],
      new OrExpression([Compare("Amount", ComparisonOperator.Equal, 1), new AndExpression([Compare("Amount", ComparisonOperator.Less, 5),
        new NotExpression(Step(RelationFixture.PaymentMeeting, RelationQuantifier.None))])]));

    foreach (var query in new[] { direct, buried }) {
      var refused = Assert.Throws<QueryPlanInvariantException>(() => _data.PaymentEntities.Query(query));
      _output.WriteLine(refused.Message);
      Assert.Contains("ExpenseConference", refused.Message);
      Assert.Contains("RL-3a", refused.Message);
      Assert.Throws<QueryPlanInvariantException>(() => _data.Db.Queries.Run(_data.Db.Queries.Plan(RelationFixture.Payments, query)));
    }
  }

  //N-6. The key set over its cap fails before the outer query runs, with the relation, the size
  //in bytes and the cap in the message — and the figure is the set's own allocation.
  [Fact]
  public void N6_TheKeySetOverItsCapFailsBeforeTheOuterQueryRuns() {
    var reports = Subscribe();
    try {
      var query = _data.PaymentEntities.Query()
        .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.NotEqual, "Nowhere"))
        .WithOptions(new QueryOptions { KeySetCapBytes = 4_096 });

      var failed = Assert.Throws<QueryCapExceededException>(() => query.Run());

      _output.WriteLine(failed.Message);
      Assert.Equal(QueryCap.KeySet, failed.Cap);
      Assert.Equal(RelationFixture.PaymentMeeting, failed.Subject);
      Assert.Equal(4_096, failed.Limit);
      Assert.True(failed.SizeReached > failed.Limit);
      Assert.Contains("'ExpenseConference'", failed.Message);
      Assert.Contains($"{failed.SizeReached} bytes", failed.Message);
      Assert.Contains("4096 bytes", failed.Message);
      //DG-3a: the inner query's partial report and the outer's, which read nothing.
      Assert.Equal(2, reports.Count);
      Assert.Equal(QueryOutcome.Failed, reports[0].Outcome);
      Assert.Equal(RelationFixture.Meetings, reports[0].CollectionName);
      Assert.Equal(QueryOutcome.Failed, reports[1].Outcome);
      Assert.Equal(RelationFixture.Payments, reports[1].CollectionName);
      Assert.Equal(0, reports[1].PagesRead);
      Assert.Equal(0, reports[1].RecordsExamined);
      Assert.Same(reports[0], Assert.Single(reports[1].InnerReports));
    } finally {
      Unsubscribe(reports);
    }
  }

  //RL-10 and S-9's join half: a record whose near column is null is related to nothing — absent
  //from Any, present in None — because its key is in no set.
  [Fact]
  public void ARecordWhoseJoinColumnIsNullIsAbsentFromAnyAndPresentInNone() {
    var any = _data.PaymentEntities.Query().WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any).Run();
    var none = _data.PaymentEntities.Query().WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.None).Run();

    _output.WriteLine(any.Report.ToString());
    _output.WriteLine(none.Report.ToString());
    var nulls = _data.PaymentRecords.Where(pair => pair.Value.ConferenceId is null).Select(pair => pair.Value.Id).Order().ToList();
    Assert.NotEmpty(nulls);
    Assert.Equal(nulls, Sorted(none.Records));
    Assert.Equal(_data.PaymentRecords.Where(pair => pair.Value.ConferenceId is not null).Select(pair => pair.Value.Id).Order(), Sorted(any.Records));
    Assert.Equal(RelationFixture.PaymentCount, any.Records.Count + none.Records.Count);
  }

  //RL-10's other half: a far collection whose join column is entirely null projects an empty set.
  [Fact]
  public void AFarColumnThatIsEntirelyNullProjectsAnEmptySet() {
    var result = _data.MeetingEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, Compare("Id", ComparisonOperator.Equal, 500))
      .Run();

    //Payment 500 has no meeting: one match, no key.
    var inner = Assert.Single(result.Report.InnerReports);
    Assert.Equal(1, inner.RecordsMatched);
    Assert.Equal(0, inner.DistinctKeys);
    Assert.Empty(result.Records);
  }

  //S-10 and RL-11: over an empty key set Any returns nothing and None returns everything the rest
  //of the query allows.
  [Fact]
  public void S10_AnEmptyKeySetIsNothingForAnyAndEverythingForNone() {
    var any = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "Nowhere"))
      .Run();
    var none = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.None, CompareText("City", ComparisonOperator.Equal, "Nowhere"))
      .Where(Compare("Amount", ComparisonOperator.Less, 100))
      .Run();

    _output.WriteLine(any.Report.ToString());
    _output.WriteLine(none.Report.ToString());
    Assert.Empty(any.Records);
    Assert.Equal(0, any.Report.IndexProbes);
    Assert.Equal(0, any.Report.RecordsExamined);
    Assert.Equal(0, Assert.Single(any.Report.InnerReports).DistinctKeys);
    Assert.Equal(_data.PaymentRecords.Count(pair => pair.Value.Amount < 100), none.Records.Count);
    Assert.All(none.Records, record => Assert.True(record.Value.Amount < 100));
  }

  // =====================================================================
  // None, and what it costs (4.3).
  // =====================================================================

  //S-7. Conferences with no expenses at all: a full scan reported as an anti-join, and correct.
  [Fact]
  public void S7_AQueryWhoseOnlyConditionIsANoneStepReportsAFullScanAsAnAntiJoin() {
    var query = _data.MeetingEntities.Query().WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.None);

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    Assert.Equal("full scan of Meeting (the only condition is an anti-join, which no index shape answers)", plan.Access.Path.Describe());
    Assert.Equal(ComparisonOperator.NotIn, Assert.Single(plan.RelationSteps).Conjunct.Operator);
    Assert.Equal(plan.Access.Path.Describe(), result.Report.AccessPath);
    var expected = Enumerable.Range(RelationFixture.MeetingCount - RelationFixture.MeetingsWithoutPayments + 1, RelationFixture.MeetingsWithoutPayments);
    Assert.Equal(expected, Sorted(result.Records));
    Assert.Equal(RelationFixture.MeetingsWithoutPayments, result.Report.DocumentsMaterialised);
    Assert.Equal(RelationFixture.MeetingCount, result.Report.RecordsExamined);
  }

  //The same with a second, indexable condition: the index is taken for that condition and the set
  //is re-checked per record.
  [Fact]
  public void ANoneStepBesideAnIndexableConditionTakesTheIndexForThatCondition() {
    var query = _data.MeetingEntities.Query()
      .Where(In("Id", 1_995, 5, 1_996))
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.None);

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    Assert.Equal("unique index seek on Meeting.Id for 3 values", plan.Access.Path.Describe());
    Assert.Equal(["Id"], plan.Access.FilterColumns);
    Assert.Equal([1_995, 1_996], Sorted(result.Records));
    Assert.Equal(3, result.Report.RecordsExamined);
  }

  //NotIn is set membership and not SQL's three-valued logic: the planner never chooses a path by
  //it, and a null satisfies it.
  [Fact]
  public void NotInIsNeverIndexableAndANullSatisfiesIt() {
    var predicate = new QueryPredicate("ConferenceId", ComparisonOperator.NotIn, ValueTypeEnum.Int, [new IntDocumentValue(1)]);
    Assert.False(predicate.IsIndexable);

    var expression = new ComparisonExpression(Column("ConferenceId"), ComparisonOperator.NotIn,
      new ConstantExpression([new IntDocumentValue(1), new IntDocumentValue(2)]), ValueTypeEnum.Int);
    var withNull = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["ConferenceId"] = new NullDocumentValue() });
    var missing = new ObjectDocumentValue(new Dictionary<string, IDocumentValue>());
    var member = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["ConferenceId"] = new IntDocumentValue(2) });
    var other = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["ConferenceId"] = new IntDocumentValue(3) });

    Assert.True(BooleanExpression.IsTrue(expression.Execute(withNull, withNull)));
    Assert.True(BooleanExpression.IsTrue(expression.Execute(missing, missing)));
    Assert.False(BooleanExpression.IsTrue(expression.Execute(member, member)));
    Assert.True(BooleanExpression.IsTrue(expression.Execute(other, other)));
    var plan = _data.PaymentEntities.Explain(new NormalizedQuery([predicate], null));
    Assert.Equal("full scan of Payment (the only condition is an anti-join, which no index shape answers)", plan.Path.Describe());
  }

  // =====================================================================
  // Two reports, not one (4.4).
  // =====================================================================

  //DG-2 and Q-10: two access paths and two page counts, and the outer count excludes the inner.
  //Between the two the executor reads the height of the index to choose the In strategy, which is
  //neither query's walk and is counted by neither.
  [Fact]
  public void ARelationQueryReportsTwoAccessPathsAndTwoPageCountsAndTheOuterExcludesTheInner() {
    var height = _data.Db.SecondaryIndex(RelationFixture.Payments, "ConferenceId").Height();
    var before = _data.Db.PageReadCount;

    var result = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .Run();

    var delta = _data.Db.PageReadCount - before;
    var inner = Assert.Single(result.Report.InnerReports);
    _output.WriteLine($"inner {inner.PagesRead}, height {height}, outer {result.Report.PagesRead}, total {delta}");
    Assert.NotEqual(inner.AccessPath, result.Report.AccessPath);
    Assert.True(inner.PagesRead > 0);
    Assert.True(result.Report.PagesRead > 0);
    Assert.Equal(delta, inner.PagesRead + height + result.Report.PagesRead);
    Assert.True(result.Report.PagesRead < delta);
  }

  //RL-12: a failure of an inner query fails the whole query, and the outer report describes the
  //inner as failed rather than as having run.
  [Fact]
  public void AnInnerFailureFailsTheWholeQuery() {
    var reports = Subscribe();
    try {
      var query = _data.PaymentEntities.Query()
        .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, new Throwing("the meeting is cursed"));

      var failed = Assert.Throws<InvalidOperationException>(() => query.Run());

      Assert.Equal("the meeting is cursed", failed.Message);
      Assert.Equal(2, reports.Count);
      Assert.Equal(QueryOutcome.Failed, reports[0].Outcome);
      Assert.Equal(QueryOutcome.Failed, reports[1].Outcome);
      Assert.Equal(0, reports[1].RecordsExamined);
      Assert.Equal(QueryOutcome.Failed, Assert.Single(reports[1].InnerReports).Outcome);
    } finally {
      Unsubscribe(reports);
    }
  }

  private sealed class Throwing : IExpression {
    private readonly string _message;

    public Throwing(string message) {
      _message = message;
    }

    public IExpression Parent { get; set; }

    public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
      throw new InvalidOperationException(_message);
    }
  }

  //RL-12 and QM-2b: the inner query runs under the outer query's lease, so a schema change started
  //during the inner query waits for the whole query.
  [Fact]
  public void TheOuterAndEveryInnerQueryRunUnderOneCatalogueLease() {
    using var file = new TempDatabaseFile();
    using var data = new RelationFixture();
    var gate = new GateExpression();
    var query = Task.Run(() => data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, new AndExpression([CompareText("City", ComparisonOperator.Equal, "Lviv"), gate]))
      .Run());
    Task change = null;
    try {
      Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(30)), "the inner query never reached its walk");
      change = Task.Run(() => data.Db.CreateIndex(RelationFixture.Meetings, "City"));
      Assert.False(change.Wait(TimeSpan.FromMilliseconds(500)), "the schema change ran inside the query's lease");
    } finally {
      gate.Release();
      try {
        query.Wait(TimeSpan.FromSeconds(30));
      } catch (AggregateException) {
        //Reported by query.Result below.
      }
    }

    var result = query.Result;
    Assert.True(change.Wait(TimeSpan.FromSeconds(30)), "the schema change never ran after the lease was given back");
    Assert.Equal("full scan of Meeting (no index on City)", Assert.Single(result.Report.InnerReports).AccessPath);
    Assert.Equal(data.MeetingsIn("Lviv").Count, result.Report.IndexProbes);
  }

  // =====================================================================
  // Direction (4.1).
  // =====================================================================

  //RL-2's two worked examples, executed: a task whose parent matches, and a task whose subtasks
  //match. Chore 3 is "urgent" and its children are 9, 10 and 11; chore 10 is "overdue" and its
  //parent is 3.
  [Fact]
  public void ASelfRelationResolvesBothWaysOnceTheWayIsStated() {
    var withUrgentParent = _data.ChoreEntities.Query()
      .WhereRelated(RelationFixture.ChoreParent, RelationQuantifier.Any, CompareText("Title", ComparisonOperator.Equal, "urgent"), RelationDirection.ToTarget)
      .OrderBy("Id").Run();
    var withOverdueSubtask = _data.ChoreEntities.Query()
      .WhereRelated(RelationFixture.ChoreParent, RelationQuantifier.Any, CompareText("Title", ComparisonOperator.Equal, "overdue"), RelationDirection.ToSource)
      .Run();
    var roots = _data.ChoreEntities.Query()
      .WhereRelated(RelationFixture.ChoreParent, RelationQuantifier.None, direction: RelationDirection.ToTarget)
      .OrderBy("Id").Run();
    var leaves = _data.ChoreEntities.Query()
      .WhereRelated(RelationFixture.ChoreParent, RelationQuantifier.None, direction: RelationDirection.ToSource)
      .OrderBy("Id").Run();

    _output.WriteLine(withUrgentParent.Report.ToString());
    Assert.Equal([9, 10, 11], withUrgentParent.Records.Select(record => record.Value.Id));
    Assert.Equal(3, Assert.Single(withOverdueSubtask.Records).Value.Id);
    Assert.Equal([1, 2, 3], roots.Records.Select(record => record.Value.Id));
    Assert.Equal(Enumerable.Range(1, 30).Where(id => !Enumerable.Range(4, 27).Select(child => child / 3).Contains(id)),
      leaves.Records.Select(record => record.Value.Id));
  }

  //RL-1: the same declared relation resolves from either side, and each side returns the records
  //the other side's predicate selects.
  [Fact]
  public void TheSameDeclaredRelationResolvesFromEitherSideAndReturnsTheRightRecords() {
    var forward = _data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "City-3")).Run();
    var reverse = _data.MeetingEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, Compare("Amount", ComparisonOperator.Equal, 999)).Run();

    var city3 = _data.MeetingsIn("City-3");
    Assert.Equal(_data.PaymentRecords.Where(pair => pair.Value.ConferenceId is { } id && city3.Contains(id)).Select(pair => pair.Value.Id).Order(),
      Sorted(forward.Records));
    Assert.Equal(_data.PaymentRecords.Where(pair => pair.Value.Amount == 999 && pair.Value.ConferenceId is { } id)
      .Select(pair => pair.Value.ConferenceId.Value).Distinct().Order(), Sorted(reverse.Records));
  }

  //Without an index on the near column the In is a scan with the membership test per record —
  //a scan chosen because no index existed, and reported as one.
  [Fact]
  public void AnInOverAnUnindexedNearColumnIsAScanThatSaysWhy() {
    using var data = new RelationFixture(indexPaymentMeeting: false);
    var result = data.PaymentEntities.Query()
      .WhereRelated(RelationFixture.PaymentMeeting, RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal("full scan of Payment (no index on ConferenceId)", result.Report.AccessPath);
    Assert.Equal(0, result.Report.IndexProbes);
    var lviv = data.MeetingsIn("Lviv");
    Assert.Equal(data.PaymentRecords.Where(pair => pair.Value.ConferenceId is { } id && lviv.Contains(id)).Select(pair => pair.Value.Id).Order(),
      Sorted(result.Records));
  }
}

//Holds a query inside its walk, lease and all, until the test lets it go. A residual, so it is
//evaluated per record against the page.
internal sealed class GateExpression : IExpression {
  private readonly ManualResetEventSlim _released = new();

  public ManualResetEventSlim Entered { get; } = new();
  public IExpression Parent { get; set; }

  public void Release() {
    _released.Set();
  }

  public IDocumentValue Execute(IDocumentValue value, IDocumentValue root) {
    Entered.Set();
    _released.Wait(TimeSpan.FromSeconds(30));
    return new BooleanDocumentValue(true);
  }
}
