using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//DC-5. Which access path each shape of query gets, and what it costs to run it.
//
//The expectations are written down rather than measured: the planner follows a rule —
//equality, then range, then a scan — and a rule whose outcome is asserted is a rule that
//cannot drift quietly into something else.
public class QueryPlannerTests {
  private const string Collection = nameof(Person);

  private readonly ITestOutputHelper _output;

  public QueryPlannerTests(ITestOutputHelper output) {
    _output = output;
  }

  private static List<ColumnDescriptor> PersonColumns() {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      //Unique, so the planner has a unique index to prefer over an ordinary one.
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("City", ValueTypeEnum.String),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ];
  }

  //Age is indexed, City is not. The difference is what separates a query that can use an
  //index from one that has to scan, and both have to be reachable from the same schema.
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, PersonColumns());
    db.CreateIndex(Collection, "Age");
    return db;
  }

  private static List<Ulid> Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Person>(Collection);
    var ids = new List<Ulid>(count);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        ids.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });
    return ids;
  }

  private static IExpression Column(string name) {
    return new PropertyExpression(name) { Parent = new RootExpression() };
  }

  private static ComparisonExpression Compare(string column, ComparisonOperator op, int value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new IntDocumentValue(value)),
      ValueTypeEnum.Int);
  }

  private static ComparisonExpression CompareText(string column, ComparisonOperator op, string value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new StringDocumentValue(value)),
      ValueTypeEnum.String);
  }

  private static NormalizedQuery Normalize(IExpression expression) {
    return QueryNormalizer.Normalize(expression);
  }

  // =====================================================================
  // The fixed set: one query shape per line, with the path it must take.
  // =====================================================================

  public static TheoryData<string, IExpression, string> PlannedQueries() => new() {
    { "an equality on an indexed column seeks",
      Compare("Age", ComparisonOperator.Equal, 30),
      "index seek on Person.Age" },
    { "an equality on the unique column seeks its unique index",
      CompareText("Name", ComparisonOperator.Equal, "Person-7"),
      "unique index seek on Person.Name" },
    { "an IN on an indexed column seeks once per value",
      new ComparisonExpression(Column("Age"), ComparisonOperator.In,
        new ConstantExpression([new IntDocumentValue(30), new IntDocumentValue(31)]), ValueTypeEnum.Int),
      "index seek on Person.Age for 2 values" },
    { "equality is preferred over a range on the same column",
      new AndExpression([
        Compare("Age", ComparisonOperator.GreaterOrEqual, 25),
        Compare("Age", ComparisonOperator.Equal, 30)]),
      "index seek on Person.Age" },
    { "equality on an indexed column beats a range, whichever column the range is on",
      new AndExpression([
        Compare("Age", ComparisonOperator.Less, 40),
        CompareText("Name", ComparisonOperator.Equal, "Person-7")]),
      "unique index seek on Person.Name" },
    { "one bound on an indexed column is a half-open range",
      Compare("Age", ComparisonOperator.GreaterOrEqual, 55),
      "index range on Person.Age [GreaterOrEqual 55]" },
    { "a between arrives as two conjuncts and becomes one bounded range",
      new AndExpression([
        Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
        Compare("Age", ComparisonOperator.LessOrEqual, 35)]),
      "index range on Person.Age [GreaterOrEqual 30, LessOrEqual 35]" },
    { "an equality on an unindexed column scans and names the column",
      CompareText("City", ComparisonOperator.Equal, "Kyiv"),
      "full scan of Person (no index on City)" },
    { "a range on an unindexed column scans",
      CompareText("City", ComparisonOperator.Greater, "K"),
      "full scan of Person (no index on City)" },
    { "a text match on an indexed column is not a range, so it scans",
      CompareText("Name", ComparisonOperator.StartsWith, "Person-1"),
      "full scan of Person (no conjunct an index can answer)" },
    { "an OR lifts nothing, so there is no conjunct to choose a path by",
      new OrExpression([
        Compare("Age", ComparisonOperator.Equal, 30),
        Compare("Age", ComparisonOperator.Equal, 31)]),
      "full scan of Person (the predicate names no column an index could be chosen by)" },
    { "a NOT is a residual too",
      new NotExpression(Compare("Age", ComparisonOperator.Equal, 30)),
      "full scan of Person (the predicate names no column an index could be chosen by)" },
    { "an OR beside an indexed equality seeks, and the OR stays behind as a residual",
      new AndExpression([
        Compare("Age", ComparisonOperator.Equal, 30),
        new OrExpression([CompareText("City", ComparisonOperator.Equal, "Kyiv"),
          CompareText("City", ComparisonOperator.Equal, "Lviv")])]),
      "index seek on Person.Age" },
    { "no predicate at all is a scan, and says that rather than blaming an index",
      null,
      "full scan of Person (no predicate)" }
  };

  [Theory]
  [MemberData(nameof(PlannedQueries))]
  public void TheChosenAccessPathIsTheExpectedOne(string description, IExpression predicate, string expected) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);

    var plan = db.Entities<Person>(Collection).Explain(Normalize(predicate));

    _output.WriteLine($"{description}: {plan}");
    Assert.Equal(expected, plan.Path.Describe());
  }

  //Identity is not a column, so it cannot arrive as a conjunct and the planner takes it from
  //beside the predicate (D-1, D-2).
  [Fact]
  public void AnIdListTakesThePrimaryIndexWhateverThePredicateSays() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 200);

    var entities = db.Entities<Person>(Collection);
    var plan = entities.Explain(Normalize(Compare("Age", ComparisonOperator.Equal, 30)), [ids[7]]);

    Assert.Equal("primary index lookup on Person by id", plan.Path.Describe());
    //The predicate did not vanish: a record fetched by id still has to satisfy it.
    Assert.Equal("Age", Assert.Single(plan.Filters).ColumnName);
  }

  // =====================================================================
  // The other half of the done-when: what a query costs to run.
  // =====================================================================

  //The rule the phase exists for. A predicate that keeps a handful of records must not cost
  //the deserialization of all of them, whichever path it took to find them.
  [Theory]
  [InlineData("Age", true)]
  [InlineData("City", false)]
  public void NoQueryMaterialisesEveryDocumentOfTheCollection(string column, bool indexed) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 2_000);

    var entities = db.Entities<Person>(Collection);
    //Numbered() gives 40 distinct ages over 2 000 records, so this keeps 50 of them; City is
    //never set, so the unindexed case keeps none. Both are a small fraction of the whole.
    var predicate = indexed
      ? (IExpression)Compare(column, ComparisonOperator.Equal, 30)
      : CompareText(column, ComparisonOperator.Equal, "Kyiv");
    var result = entities.Query(Normalize(predicate));

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(indexed, !result.Report.AccessPath.StartsWith("full scan"));
    //Only the records that matched were turned into documents. The scan looks at every
    //record — that is what a scan is — but it reads one field of each rather than all of it.
    Assert.Equal(result.Report.RecordsMatched, result.Report.DocumentsMaterialised);
    Assert.True(result.Report.DocumentsMaterialised < 2_000,
      $"{result.Report.DocumentsMaterialised} documents materialised out of 2 000");
    Assert.Equal(result.Records.Count, result.Report.RecordsMatched);
  }

  //DC-5's acceptance criterion: the same collection reached through an index and through a
  //scan, with the index reading far fewer pages. Selective on purpose — see the crossover
  //test below for why that word is doing real work.
  [Fact]
  public void ASelectiveIndexedQueryReadsFarFewerPagesThanTheScanItReplaces() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 20_000);

    var entities = db.Entities<Person>(Collection);
    var seek = entities.Query(Normalize(CompareText("Name", ComparisonOperator.Equal, "Person-7777")));
    //The same collection with no conjunct an index can answer: a scan, so the two are
    //comparable.
    var scan = entities.Query(Normalize(CompareText("Name", ComparisonOperator.EndsWith, "-7777")));

    _output.WriteLine($"seek: {seek.Report}");
    _output.WriteLine($"scan: {scan.Report}");
    Assert.Equal("Person-7777", Assert.Single(seek.Records).Value.Name);
    Assert.Equal("Person-7777", Assert.Single(scan.Records).Value.Name);
    Assert.True(seek.Report.PagesRead * 20 < scan.Report.PagesRead,
      $"the seek read {seek.Report.PagesRead} pages and the scan {scan.Report.PagesRead}");
    //One record examined against every record in the collection: the difference the index
    //makes is in what never had to be looked at.
    Assert.Equal(1, seek.Report.RecordsExamined);
    Assert.Equal(20_000, scan.Report.RecordsExamined);
  }

  //The measurement that qualifies the rule, kept as a test because it is evidence rather than
  //a defect.
  //
  //"Equality first, then range, then a scan" is a rule without statistics, and it is wrong
  //here: an equality matching one record in forty reads a page per match, because the matches
  //are scattered and nothing caches a page between two reads of it, while a scan gets some
  //forty records out of every page it reads. The index wins on records examined either way —
  //what it loses is page reads, and only once the answer stops being selective.
  //
  //The crossover is therefore about where the records per page sits, and a cost model would
  //need per-value counts to find it. Nothing collects them, so the planner follows the rule
  //and the report says what the rule chose (UI-4); the number below is what a Phase 8 cost
  //model would have to beat.
  [Fact]
  public void AnIndexSeekOverManyMatchesCanCostMorePagesThanAScan() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 20_000);

    var entities = db.Entities<Person>(Collection);
    //Ages cycle over 40 values, so this matches 500 of 20 000 — one in forty.
    var seek = entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, 30)));
    var scan = entities.Query(Normalize(Compare("Age", ComparisonOperator.NotEqual, -1)));

    _output.WriteLine($"seek over 500 matches: {seek.Report}");
    _output.WriteLine($"scan over the collection: {scan.Report}");
    Assert.Equal(500, seek.Records.Count);
    Assert.Equal(20_000, scan.Records.Count);
    //The claim the planner is making when it prefers the seek, and the one it is not.
    Assert.True(seek.Report.RecordsExamined * 20 < scan.Report.RecordsExamined,
      "the seek should examine far fewer records");
    Assert.True(seek.Report.PagesRead > scan.Report.PagesRead / 2,
      $"the seek read {seek.Report.PagesRead} pages against the scan's {scan.Report.PagesRead}; " +
      "if this has become a comfortable win, a page cache landed and the rule can be revisited");
  }

  //A range walks the leaves between its bounds and reads the records they address, and it
  //must not read the ones outside them.
  [Fact]
  public void ARangeExaminesOnlyTheRecordsBetweenItsBounds() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 4_000);

    var result = db.Entities<Person>(Collection).Query(Normalize(new AndExpression([
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
      Compare("Age", ComparisonOperator.LessOrEqual, 34)])));

    _output.WriteLine(result.Report.ToString());
    //Ages run 20..59 over 4 000 records: 100 records per age, five ages in the range.
    Assert.Equal(500, result.Records.Count);
    Assert.Equal(500, result.Report.RecordsExamined);
    Assert.All(result.Records, record => Assert.InRange(record.Value.Age, 30, 34));
  }

  //A range is not exact — it covers more than the predicate — so the bounds stay in the
  //per-record filters. An equality on an integer is exact and does not.
  [Fact]
  public void AnExactSeekDropsItsConjunctAndAnInexactPathKeepsIt() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);
    var entities = db.Entities<Person>(Collection);

    var seek = entities.Explain(Normalize(Compare("Age", ComparisonOperator.Equal, 30)));
    Assert.True(seek.Path.IsExact);
    Assert.Empty(seek.Filters);

    var range = entities.Explain(Normalize(Compare("Age", ComparisonOperator.GreaterOrEqual, 30)));
    Assert.False(range.Path.IsExact);
    Assert.Single(range.Filters);
  }

  //D-3: a string key is folded before it is stored, so an entry it matches is a candidate
  //rather than an answer. The seek is still used; the predicate is re-checked on top of it.
  [Fact]
  public void ASeekOnAFoldedStringKeyIsNarrowedButStillRechecked() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);

    var plan = db.Entities<Person>(Collection)
      .Explain(Normalize(CompareText("Name", ComparisonOperator.Equal, "Person-7")));

    Assert.Equal("unique index seek on Person.Name", plan.Path.Describe());
    Assert.False(plan.Path.IsExact);
    Assert.Equal("Name", Assert.Single(plan.Filters).ColumnName);
  }

  //The folding is not theoretical: the index holds the folded key, so a query written in the
  //other case finds the record and the re-check does not throw the answer away again.
  [Fact]
  public void AQueryFindsARecordWhoseIndexedTextDiffersOnlyInCase() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>(Collection);
    db.InTransaction(() => entities.Insert(new Person {
      Id = 1, Name = "Олена", Age = 31, Passport = new Passport("ST-1"), Tags = []
    }));

    var result = entities.Query(Normalize(CompareText("Name", ComparisonOperator.Equal, "олена")));

    Assert.Equal("unique index seek on Person.Name", result.Report.AccessPath);
    Assert.Equal("Олена", Assert.Single(result.Records).Value.Name);
  }

  //A residual is the part of the predicate no conjunct could carry. It has to be applied, or
  //the access path's extra records would come back as answers.
  [Fact]
  public void AResidualIsAppliedToTheRecordsTheAccessPathReturned() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 400);

    //Age = 30 seeks; the OR on top of it can only be checked per record.
    var result = db.Entities<Person>(Collection).Query(Normalize(new AndExpression([
      Compare("Age", ComparisonOperator.Equal, 30),
      new OrExpression([
        Compare("Id", ComparisonOperator.Equal, 10),
        Compare("Id", ComparisonOperator.Equal, 50)])])));

    _output.WriteLine(result.Report.ToString());
    Assert.True(result.Report.HasResidual);
    Assert.Equal(10, result.Report.RecordsExamined);
    Assert.Equal([10, 50], result.Records.Select(record => record.Value.Id).Order());
  }

  //A dead image is still on the page until compaction takes it. Neither the index entries nor
  //the scan may hand one back.
  [Fact]
  public void ADeletedRecordIsNotReturnedByAnyAccessPath() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 200);
    var entities = db.Entities<Person>(Collection);
    var deleted = ids[30];
    entities.Delete(deleted);

    var byId = entities.Query(NormalizedQuery.Everything, [deleted]);
    var seek = entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, 30)));
    var scan = entities.Query(Normalize(CompareText("City", ComparisonOperator.NotEqual, "Kyiv")));

    Assert.Empty(byId.Records);
    Assert.DoesNotContain(seek.Records, record => record.RecordId == deleted);
    Assert.Equal(199, scan.Records.Count);
  }

  //An updated record is written somewhere else (VR-12), and the index entry is repointed. A
  //query must find it once, at its new value.
  [Fact]
  public void AnUpdatedRecordIsFoundAtItsNewValueAndNotAtItsOld() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 200);
    var entities = db.Entities<Person>(Collection);
    var moved = ids[30];
    var person = entities.GetById(moved).Value;
    var oldAge = person.Age;
    person.Age = 99;
    entities.Update(moved, person);

    var atTheOldAge = entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, oldAge)));
    var atTheNewAge = entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, 99)));

    Assert.DoesNotContain(atTheOldAge.Records, record => record.RecordId == moved);
    Assert.Equal(moved, Assert.Single(atTheNewAge.Records).RecordId);
  }

  //A record too big for its page keeps its body in an overflow chain (ST-5). The predicate is
  //checked against the reassembled image, so a query has to see the same fields either way.
  [Fact]
  public void ARecordWithAnOverflowBodyIsFilteredLikeAnyOther() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>(Collection);
    db.InTransaction(() => {
      entities.Insert(new Person {
        Id = 1, Name = "Big", Age = 77, Passport = new Passport(new string('x', 20_000)),
        Tags = [new Tag("tag")]
      });
      entities.Insert(TestPeople.Numbered(2));
    });

    var result = entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, 77)));

    Assert.Equal("Big", Assert.Single(result.Records).Value.Name);
  }

  //"Where the field is reachable without deserializing the whole document": a path one level
  //down parses the sub-object it names and nothing beside it. It is a residual rather than a
  //conjunct — a path into a document names no column an index could be chosen by — so this is
  //the residual half being evaluated against the page as well.
  [Fact]
  public void AResidualOverANestedFieldIsEvaluatedWithoutBuildingTheWholeDocument() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 500);

    var passportCode = new PropertyExpression("Code") {
      Parent = new PropertyExpression("Passport") { Parent = new RootExpression() }
    };
    var result = db.Entities<Person>(Collection).Query(QueryNormalizer.Normalize(
      new ComparisonExpression(passportCode, ComparisonOperator.Equal,
        new ConstantExpression(new StringDocumentValue("ST-000123")), ValueTypeEnum.String)));

    _output.WriteLine(result.Report.ToString());
    Assert.True(result.Report.HasResidual);
    Assert.Equal(123, Assert.Single(result.Records).Value.Id);
    //Every record was examined — there is no index to narrow by — and exactly one became a
    //document. The other 499 cost the Passport sub-object and nothing else.
    Assert.Equal(500, result.Report.RecordsExamined);
    Assert.Equal(1, result.Report.DocumentsMaterialised);
  }

  //Every query publishes its report, which is what UI-4 asks the engine for. Subscribing once
  //has to be enough — a call site that forgets to report is the failure mode this avoids.
  [Fact]
  public void EveryQueryPublishesItsReport() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 100);
    var reports = new List<QueryReport>();
    db.Queries.QueryExecuted += reports.Add;

    var entities = db.Entities<Person>(Collection);
    entities.Query(Normalize(Compare("Age", ComparisonOperator.Equal, 30)));
    entities.Query(Normalize(CompareText("City", ComparisonOperator.Equal, "Kyiv")));

    Assert.Equal(2, reports.Count);
    Assert.Equal("index seek on Person.Age", reports[0].AccessPath);
    Assert.Equal("full scan of Person (no index on City)", reports[1].AccessPath);
    Assert.All(reports, report => Assert.True(report.PagesRead >= 0));
  }
}
