using System.Collections.Immutable;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

public sealed class Townsperson {
  public int Id { get; set; }
  public string City { get; set; }
  public int Age { get; set; }
}

//Step 2.2: the order taken from the index walk (OR-2, OR-2a, Q-3, Q-14). The rule is the prefix
//rule, stated the right way round — the key must refine the request — and the planner may choose
//an ordered walk for the order's sake by a stated rule.
public class OrderFromIndexWalkTests {
  private const string Town = nameof(Townsperson);
  private static readonly string[] Cities = ["Lviv", "Kyiv", "Odesa", "Kharkiv", "Dnipro"];

  private readonly ITestOutputHelper _output;

  public OrderFromIndexWalkTests(ITestOutputHelper output) {
    _output = output;
  }

  //City and Age are both indexed and Id is not: the two-index shape of S-1a.
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Town, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("City", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int)
    ]);
    db.CreateIndex(Town, "City");
    db.CreateIndex(Town, "Age");
    return db;
  }

  private static List<(Ulid Id, Townsperson Value)> Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Townsperson>(Town);
    var records = new List<(Ulid, Townsperson)>(count);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        var person = new Townsperson { Id = i, City = Cities[i % Cities.Length], Age = 20 + (i * 7) % 50 };
        records.Add((entities.Insert(person), person));
      }
    });
    return records;
  }

  // =====================================================================
  // The rule itself (OR-2).
  // =====================================================================

  private static ImmutableArray<OrderColumn> Order(params string[] columns) {
    return columns.Select(column => column.EndsWith(" desc")
        ? new OrderColumn(column[..^5], OrderDirection.Descending)
        : new OrderColumn(column))
      .ToImmutableArray();
  }

  private static readonly QueryPredicate DatePredicate =
    new("Date", ComparisonOperator.Equal, ValueTypeEnum.DateTime, [new DateTimeDocumentValue(DateTime.UnixEpoch)]);

  public static TheoryData<string, AccessPath, string[], bool> WalkCases() => new() {
    { "a key of (date, identity) satisfies a request for (date, identity)",
      new IndexRangePath("Event", "Date", [DatePredicate], null, null, "open"), ["Date"], true },
    { "a key of (date, identity) does not satisfy (date, cost, identity): the key must refine the request",
      new IndexRangePath("Event", "Date", [DatePredicate], null, null, "open"), ["Date", "Cost"], false },
    { "a descending request is never a prefix of a forward walk (Q-3)",
      new IndexRangePath("Event", "Date", [DatePredicate], null, null, "open"), ["Date desc"], false },
    { "a request for another column is not a prefix",
      new IndexRangePath("Event", "Date", [DatePredicate], null, null, "open"), ["Cost"], false },
    { "a seek yields the key order too, because the executor visits its values in key order",
      new IndexSeekPath("Event", "Date", DatePredicate, false), ["Date"], true },
    { "an ordered walk chosen for the order yields it",
      new OrderedIndexWalkPath("Event", "Date"), ["Date"], true },
    { "a full scan has no key order (the unreachable row of PG-3a)",
      new FullScanPath("Event", "no predicate"), ["Date"], false },
    { "a lookup by ids yields the ids' order, which is no key order",
      new PrimaryKeyPath("Event", [Ulid.NewUlid()]), ["Date"], false },
    { "a membership pass is a scan and has no key order",
      new MembershipPassPath("Event", "Date", DatePredicate, new KeySet("R", long.MaxValue), "test"), ["Date"], false }
  };

  [Theory]
  [MemberData(nameof(WalkCases))]
  public void TheWalkSatisfiesTheRequestOnlyWhenTheRequestIsAPrefixOfTheKeyOrder(string description, AccessPath path,
      string[] order, bool satisfied) {
    _output.WriteLine(description);
    Assert.Equal(satisfied, OrderRules.WalkSatisfies(path, Order(order)));
  }

  // =====================================================================
  // The engine (OR-2).
  // =====================================================================

  //The predicate's own path is a range over the Age index, whose key order (Age, identity) the
  //requested order is a prefix of: the walk is the order (OR-2). With no predicate at all the
  //same page comes from a walk chosen for the order (OR-2a); both retain nothing.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void AnOrderedQueryTheKeySatisfiesRetainsNothingInTheOrderingStage(bool withPredicate) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 500);
    var query = db.Entities<Townsperson>(Town).Query().OrderBy("Age").Skip(20).Take(20);
    if (withPredicate) {
      query = query.Where(Compare("Age", ComparisonOperator.GreaterOrEqual, 20));
    }

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    Assert.Equal(OrderSource.IndexWalk, plan.OrderSource);
    if (withPredicate) {
      Assert.Equal("index range on Townsperson.Age [GreaterOrEqual 20]", plan.Access.Path.Describe());
      Assert.Contains("prefix of it (OR-2)", plan.OrderReason);
    } else {
      Assert.IsType<OrderedIndexWalkPath>(plan.Access.Path);
      Assert.Contains("chosen for the order (OR-2a)", plan.OrderReason);
    }
    Assert.Equal(OrderSource.IndexWalk, result.Report.OrderSource);
    Assert.Equal(0, result.Report.RecordsRetained);
    Assert.True(result.Report.IsStreaming);
    Assert.Equal(40, result.Report.RecordsExamined);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    var expected = records.OrderBy(pair => pair.Value.Age).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Skip(20).Take(20);
    Assert.Equal(expected.Select(pair => pair.Id), result.Records.Select(record => record.RecordId));
  }

  [Fact]
  public void AQueryAskingForOneColumnMoreThanTheKeyResolvesReportsAStageInstead() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 500);
    var query = db.Entities<Townsperson>(Town).Query().OrderBy("Age").ThenBy("Id").Take(20);

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    Assert.Equal(OrderSource.BoundedHeap, plan.OrderSource);
    Assert.Contains("identity order and not in Id order", plan.OrderReason);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.Equal(20, result.Report.RecordsRetained);
    Assert.False(result.Report.IsStreaming);
    Assert.Equal(records.OrderBy(pair => pair.Value.Age).ThenBy(pair => pair.Value.Id).Take(20).Select(pair => pair.Id),
      result.Records.Select(record => record.RecordId));
  }

  //An In over several values is one walk of the stretches they occupy, visited in key order and
  //each once, so a request for the seek's column is satisfied.
  [Fact]
  public void AnInSeekYieldsTheKeyOrderWhateverOrderTheQueryWroteItsValuesIn() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 500);

    var result = db.Entities<Townsperson>(Town).Query().Where(In("Age", 41, 27, 41, 34)).OrderBy("Age").Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(OrderSource.IndexWalk, result.Report.OrderSource);
    Assert.Equal(3, result.Report.IndexProbes);
    Assert.Equal(records.Where(pair => pair.Value.Age is 27 or 34 or 41)
        .OrderBy(pair => pair.Value.Age).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Select(pair => pair.Id),
      result.Records.Select(record => record.RecordId));
  }

  // =====================================================================
  // The choice between two paths (OR-2a, S-1a).
  // =====================================================================

  //S-1a. Where(City = 'Lviv').OrderBy(Age).Skip(20).Take(20) with an index on each: there is no
  //free answer. The rule keeps the predicate's path, because a seek is selective; forced the other
  //way the planner walks Age in order and filters City; both return the same records in the same
  //order, and each plan says which it chose and why.
  [Fact]
  public void S1a_ThePlanNamesWhichPathItChoseAndBothChoicesReturnTheSameRecordsInTheSameOrder() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 1_000);
    var query = db.Entities<Townsperson>(Town).Query()
      .Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Age").Skip(20).Take(20);

    var byRule = query.Explain();
    var seek = query.WithOptions(new QueryOptions { PathChoice = PathChoice.PredicatePath });
    var walk = query.WithOptions(new QueryOptions { PathChoice = PathChoice.OrderedWalk });
    var seekPlan = seek.Explain();
    var walkPlan = walk.Explain();
    var seekResult = seek.Run();
    var walkResult = walk.Run();

    _output.WriteLine($"by rule: {byRule}");
    _output.WriteLine($"seek: {seekResult.Report}");
    _output.WriteLine($"walk: {walkResult.Report}");
    Assert.Equal("index seek on Townsperson.City", byRule.Access.Path.Describe());
    Assert.Contains("is selective", byRule.PathReason);
    Assert.Equal(OrderSource.BoundedHeap, byRule.OrderSource);
    Assert.Equal(byRule.Access.Path.Describe(), seekPlan.Access.Path.Describe());
    Assert.Equal("ordered walk of the index on Townsperson.Age", walkPlan.Access.Path.Describe());
    Assert.Equal(OrderSource.IndexWalk, walkPlan.OrderSource);
    Assert.Contains("forced", walkPlan.PathReason);
    Assert.Equal(["City"], walkPlan.Access.FilterColumns);

    var expected = records.Where(pair => pair.Value.City == "Lviv")
      .OrderBy(pair => pair.Value.Age).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Skip(20).Take(20)
      .Select(pair => pair.Id).ToList();
    Assert.Equal(expected, seekResult.Records.Select(record => record.RecordId));
    Assert.Equal(expected, walkResult.Records.Select(record => record.RecordId));
    //The costs differ, which is what the plan's line exists to show: the seek examines every
    //Lviv record and the walk examines index entries until the page is full.
    Assert.Equal(200, seekResult.Report.RecordsExamined);
    Assert.Equal(40, seekResult.Report.RecordsRetained);
    Assert.Equal(0, walkResult.Report.RecordsRetained);
    Assert.Equal(40, walkResult.Report.RecordsMatched);
    Assert.True(walkResult.Report.RecordsExamined > 40, "the walk filtered what it passed");
  }

  //The rule's other branch: bounded by Take and the predicate's path is not selective — a scan,
  //or a range — so the planner walks the index in order and filters.
  [Theory]
  [InlineData("scan")]
  [InlineData("range")]
  public void TheRuleWalksInOrderWhenTheQueryIsBoundedAndThePredicatePathIsNotSelective(string shape) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 1_000);
    var predicate = shape == "scan"
      ? Compare("Id", ComparisonOperator.GreaterOrEqual, 100)
      : CompareText("City", ComparisonOperator.GreaterOrEqual, "Kyiv");
    var query = db.Entities<Townsperson>(Town).Query().Where(predicate).OrderBy("Age").Take(20);

    var plan = query.Explain();
    var result = query.Run();
    var unbounded = query.Take(long.MaxValue).Explain();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    Assert.IsType<OrderedIndexWalkPath>(plan.Access.Path);
    Assert.Contains("is not selective", plan.PathReason);
    Assert.Equal(OrderSource.IndexWalk, plan.OrderSource);
    Assert.True(result.Report.RecordsExamined < records.Count, "the walk stopped at the page");
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    var matching = records.Where(pair => shape == "scan" ? pair.Value.Id >= 100 : string.CompareOrdinal(pair.Value.City.ToLowerInvariant(), "kyiv") >= 0);
    Assert.Equal(matching.OrderBy(pair => pair.Value.Age).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Take(20).Select(pair => pair.Id),
      result.Records.Select(record => record.RecordId));
    //Still bounded, so still the walk: Take(long.MaxValue) is a Take.
    Assert.IsType<OrderedIndexWalkPath>(unbounded.Access.Path);
  }

  [Fact]
  public void AnOrderedQueryWithNoTakeKeepsThePredicatePathAndSortsCompletely() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 300);
    var query = db.Entities<Townsperson>(Town).Query()
      .Where(Compare("Id", ComparisonOperator.GreaterOrEqual, 100)).OrderBy("Age");

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    Assert.IsType<FullScanPath>(plan.Access.Path);
    Assert.Contains("no Take", plan.PathReason);
    Assert.Equal(OrderSource.CompleteSort, plan.OrderSource);
    Assert.Equal(200, result.Report.RecordsRetained);
    Assert.Equal(200, result.Records.Count);
  }

  //A list of ids is answered by the primary index and by nothing else, so an ordered walk cannot
  //apply it — not even when forced.
  [Fact]
  public void AWalkCannotServeAQueryRestrictedToAListOfIds() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var records = Fill(db, 300);
    var chosen = records.Where(pair => pair.Value.Id % 7 == 0).Select(pair => pair.Id).ToList();
    var query = db.Entities<Townsperson>(Town).Query().WhereIdIn(chosen).OrderBy("Age").Take(10)
      .WithOptions(new QueryOptions { PathChoice = PathChoice.OrderedWalk });

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    Assert.IsType<PrimaryKeyPath>(plan.Access.Path);
    Assert.Equal(OrderSource.BoundedHeap, plan.OrderSource);
    Assert.Equal(10, result.Records.Count);
    Assert.All(result.Records, record => Assert.Equal(0, record.Value.Id % 7));
  }
}
