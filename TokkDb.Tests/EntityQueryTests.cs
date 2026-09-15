using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

public class Conference {
  public int Id { get; set; }
  public string City { get; set; }
}

public class Expense {
  public int Id { get; set; }
  public int ConferenceId { get; set; }
  public int Amount { get; set; }
}

public class TaskItem {
  public int Id { get; set; }
  public int ParentId { get; set; }
}

//The entity query builder of Phase 1: the refusals a request raises about itself when it is
//built, the ones only the catalogue can raise when it is planned, and the boundary between a plan
//and its execution (Q-11, QM-2 to QM-5).
public class EntityQueryTests {
  private const string People = nameof(Person);
  private const string Conferences = nameof(Conference);
  private const string Expenses = nameof(Expense);
  private const string Tasks = nameof(TaskItem);

  private readonly ITestOutputHelper _output;

  public EntityQueryTests(ITestOutputHelper output) {
    _output = output;
  }

  //Age is indexed and City is not, as in QueryPlannerTests; Passport is an object and has no order.
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(People, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("City", ValueTypeEnum.String),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    db.CreateIndex(People, "Age");
    return db;
  }

  //Two collections joined one way, and one joined to itself.
  private static TokkDbConnection NewRelatedDatabase(TempDatabaseFile file) {
    var db = NewDatabase(file);
    db.CreateCollection(Conferences, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int, unique: true),
      new ColumnDescriptor("City", ValueTypeEnum.String)
    ]);
    db.CreateCollection(Expenses, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("ConferenceId", ValueTypeEnum.Int),
      new ColumnDescriptor("Amount", ValueTypeEnum.Int)
    ]);
    db.CreateCollection(Tasks, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int, unique: true),
      new ColumnDescriptor("ParentId", ValueTypeEnum.Int)
    ]);
    db.CreateRelation("ExpenseConference", Expenses, "ConferenceId", Conferences, "Id");
    db.CreateRelation("TaskParent", Tasks, "ParentId", Tasks, "Id");
    return db;
  }

  private static List<Ulid> Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Person>(People);
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

  private static RelationStepExpression Step(string relation, RelationQuantifier quantifier = RelationQuantifier.Any,
      IExpression inner = null) {
    //The resolved names a binder would put here are not what the planner goes by: it resolves the
    //relation from the catalogue, by name.
    return new RelationStepExpression(relation, "unused", "unused", "unused", quantifier, inner);
  }

  // =====================================================================
  // Build time (QM-4): refused from the request alone, before any planner.
  // =====================================================================

  public static TheoryData<string, Func<DbQuery<Person>, DbQuery<Person>>, QueryRequestRefusal, string[]> BuildRefusals() => new() {
    { "N-1: Skip with no order",
      query => query.Skip(5).Take(10),
      QueryRequestRefusal.SkipWithoutOrder, ["Skip(5)", "needs an order"] },
    { "a negative Skip",
      query => query.OrderBy("Age").Skip(-1),
      QueryRequestRefusal.NegativeSkip, ["Skip(-1)", "negative"] },
    { "a negative Take",
      query => query.Take(-3),
      QueryRequestRefusal.NegativeTake, ["Take(-3)", "negative"] },
    { "N-4: a relation step inside the predicate of a relation step, beside the predicate",
      query => query.WhereRelated("ExpenseConference", RelationQuantifier.Any, Step("TaskParent")),
      QueryRequestRefusal.NestedRelationStep, ["ExpenseConference", "TaskParent", "one hop"] },
    { "N-4: a relation step inside a relation step, in the predicate, under an Or",
      query => query.Where(new AndExpression([
        Compare("Age", ComparisonOperator.Equal, 30),
        Step("ExpenseConference", inner: new OrExpression([Compare("Amount", ComparisonOperator.Less, 5), Step("TaskParent")]))])),
      QueryRequestRefusal.NestedRelationStep, ["ExpenseConference", "TaskParent"] },
    { "N-7a: a relation step under an Or",
      query => query.Where(new OrExpression([Compare("Age", ComparisonOperator.Equal, 30), Step("ExpenseConference")])),
      QueryRequestRefusal.RelationStepOutsideConjunction, ["ExpenseConference", "under an Or", "AND-conjunctive"] },
    { "a relation step under a Not",
      query => query.Where(new NotExpression(Step("ExpenseConference"))),
      QueryRequestRefusal.RelationStepOutsideConjunction, ["ExpenseConference", "under a Not"] },
    { "a relation step under an And under an Or is still under the Or",
      query => query.Where(new AndExpression([
        Compare("Age", ComparisonOperator.Equal, 30),
        new OrExpression([Compare("Age", ComparisonOperator.Equal, 31),
          new AndExpression([Compare("Id", ComparisonOperator.Equal, 1), Step("ExpenseConference")])])])),
      QueryRequestRefusal.RelationStepOutsideConjunction, ["ExpenseConference", "under an Or"] }
  };

  //"None of them reaches a planner": the entities are over a collection that does not exist, so a
  //refusal that waited for the planner would come out as EntityNotFoundException instead.
  [Theory]
  [MemberData(nameof(BuildRefusals))]
  public void ARequestThatIsWrongOnItsOwnTermsIsRefusedWhenBuiltAndNeverPlanned(string description,
      Func<DbQuery<Person>, DbQuery<Person>> write, QueryRequestRefusal refusal, string[] named) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var query = write(db.Entities<Person>("NoSuchCollection").Query());

    var built = Assert.Throws<QueryRequestRefusedException>(() => query.Build());
    var explained = Assert.Throws<QueryRequestRefusedException>(() => query.Explain());
    var run = Assert.Throws<QueryRequestRefusedException>(() => query.Run());

    _output.WriteLine($"{description}: {built.Message}");
    Assert.Equal(refusal, built.Refusal);
    Assert.All(named, name => Assert.Contains(name, built.Message));
    Assert.Equal(built.Message, explained.Message);
    Assert.Equal(built.Message, run.Message);
  }

  //N-5 and RL-7: refused with the reason, not answered with an empty result.
  [Fact]
  public void AllIsRefusedWithTheReasonWhereverTheStepIsWritten() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var entities = db.Entities<Person>("NoSuchCollection");

    var beside = entities.Query().WhereRelated("ExpenseConference", RelationQuantifier.All);
    var inside = entities.Query().Where(Step("ExpenseConference", RelationQuantifier.All));

    foreach (var query in new[] { beside, inside }) {
      var refused = Assert.Throws<NotSupportedException>(() => query.Build());
      Assert.Throws<NotSupportedException>(() => query.Run());
      Assert.Contains("ExpenseConference", refused.Message);
      Assert.Contains("All quantifier", refused.Message);
      Assert.Contains("holds a single value, so there is nothing for it to quantify over", refused.Message);
    }
  }

  //RL-3's other half: a conjunction, however nested, is where a step is required on its own, so
  //it is lifted rather than refused.
  [Fact]
  public void ARelationStepInAConjunctionIsLiftedOutOfThePredicate() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);

    var request = db.Entities<Person>(People).Query()
      .Where(new AndExpression([
        Compare("Age", ComparisonOperator.Greater, 20),
        new AndExpression([Compare("Id", ComparisonOperator.Less, 50),
          Step("ExpenseConference", RelationQuantifier.None, Compare("Amount", ComparisonOperator.Greater, 500))])]))
      .Build();

    Assert.Equal(["Age", "Id"], request.Query.Conjuncts.Select(conjunct => conjunct.ColumnName));
    Assert.Null(request.Query.Residual);
    var step = Assert.Single(request.RelationSteps);
    Assert.Equal(RelationQuantifier.None, step.Quantifier);
    Assert.Equal("Amount", Assert.Single(step.Inner.Conjuncts).ColumnName);
  }

  //ThenBy has nothing to extend on a query with no order. That is a misuse of the builder, so it
  //is refused where it is written rather than when the request is built.
  [Fact]
  public void ThenByWithNoOrderToExtendIsRefusedWhereItIsWritten() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var query = db.Entities<Person>(People).Query();

    Assert.Contains("OrderBy", Assert.Throws<InvalidOperationException>(() => query.ThenBy("Age")).Message);
    Assert.Throws<InvalidOperationException>(() => query.ThenByDescending("Age"));
    Assert.Equal(2, query.OrderBy("Age").ThenByDescending("Name").Build().Order.Length);
  }

  // =====================================================================
  // Plan time (QM-4a): refused against the catalogue, identically by Explain and Run.
  // =====================================================================

  //The same query, explained and run.
  public sealed record Written(Func<QueryRequestPlan> Explain, Action Run);

  private static Func<TokkDbConnection, Written> Over<T>(string collection, Func<DbQuery<T>, DbQuery<T>> write) {
    return db => {
      var query = write(db.Entities<T>(collection).Query());
      return new Written(() => query.Explain(), () => query.Run());
    };
  }

  public static TheoryData<string, Func<TokkDbConnection, Written>, QueryPlanRefusal, string[]> PlanRefusals() => new() {
    { "N-2: an order on a column the collection does not have",
      Over<Expense>(Expenses, query => query.OrderBy("Amount").ThenBy("Cost")),
      QueryPlanRefusal.UnknownOrderColumn, ["'Cost'", "'Expense'", "Id, ConferenceId, Amount"] },
    { "an order on a column with no ordering",
      Over<Person>(People, query => query.OrderBy("Passport")),
      QueryPlanRefusal.UnorderableOrderColumn, ["'Passport'", "Object"] },
    { "N-3: an unknown relation, naming the ones that exist",
      Over<Expense>(Expenses, query => query.WhereRelated("ExpenseEvent", RelationQuantifier.Any)),
      QueryPlanRefusal.UnknownRelation, ["'ExpenseEvent'", "Declared: ExpenseConference, TaskParent."] },
    { "N-7: a self-relation with no direction",
      Over<TaskItem>(Tasks, query => query.WhereRelated("TaskParent", RelationQuantifier.Any)),
      QueryPlanRefusal.SelfRelationWithoutDirection, ["'TaskParent'", "'TaskItem' to itself", "ToTarget", "ToSource"] },
    { "N-7: a self-relation step lifted out of the predicate has no direction either",
      Over<TaskItem>(Tasks, query => query.Where(Step("TaskParent"))),
      QueryPlanRefusal.SelfRelationWithoutDirection, ["'TaskItem' to itself"] },
    { "a stated direction the collection is not on",
      Over<Expense>(Expenses, query => query.WhereRelated("ExpenseConference", RelationQuantifier.Any,
        direction: RelationDirection.ToSource)),
      QueryPlanRefusal.RelationDirectionMismatch, ["'ExpenseConference'", "'Expense'", "ToTarget, not ToSource"] },
    { "a relation the collection is on neither side of",
      Over<Person>(People, query => query.WhereRelated("ExpenseConference", RelationQuantifier.None)),
      QueryPlanRefusal.RelationDirectionMismatch, ["'ExpenseConference'", "'Person' is on neither side"] }
  };

  [Theory]
  [MemberData(nameof(PlanRefusals))]
  public void ANameTheCatalogueDoesNotBearOutIsRefusedWhenPlannedByExplainAndRunAlike(string description,
      Func<TokkDbConnection, Written> write, QueryPlanRefusal refusal, string[] named) {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    var query = write(db);

    //It builds: nothing about it is wrong until there is a catalogue to check it against.
    var explained = Assert.Throws<QueryPlanRefusedException>(() => query.Explain());
    var run = Assert.Throws<QueryPlanRefusedException>(() => query.Run());

    _output.WriteLine($"{description}: {explained.Message}");
    Assert.Equal(refusal, explained.Refusal);
    Assert.All(named, name => Assert.Contains(name, explained.Message));
    Assert.Equal(explained.Refusal, run.Refusal);
    Assert.Equal(explained.Message, run.Message);
  }

  //RL-1 and RL-2: one declared relation resolves from either side, and a self-relation resolves
  //both ways once the way is stated.
  [Fact]
  public void ARelationStepResolvesFromWhicheverSideTheQueryIsOn() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);

    var forward = Assert.Single(db.Entities<Expense>(Expenses).Query()
      .WhereRelated("ExpenseConference", RelationQuantifier.Any, CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .Explain().RelationSteps);
    var reverse = Assert.Single(db.Entities<Conference>(Conferences).Query()
      .WhereRelated("ExpenseConference", RelationQuantifier.Any, Compare("Amount", ComparisonOperator.Greater, 500))
      .Explain().RelationSteps);
    var parent = Assert.Single(db.Entities<TaskItem>(Tasks).Query()
      .WhereRelated("TaskParent", RelationQuantifier.Any, direction: RelationDirection.ToTarget)
      .Explain().RelationSteps);
    var subtasks = Assert.Single(db.Entities<TaskItem>(Tasks).Query()
      .WhereRelated("TaskParent", RelationQuantifier.Any, direction: RelationDirection.ToSource)
      .Explain().RelationSteps);

    Assert.Equal((RelationDirection.ToTarget, "ConferenceId", Conferences, "Id"),
      (forward.Direction, forward.NearColumn, forward.FarCollection, forward.FarColumn));
    Assert.Equal("full scan of Conference (no index on City)", forward.Inner.Path.Describe());
    Assert.Equal((RelationDirection.ToSource, "Id", Expenses, "ConferenceId"),
      (reverse.Direction, reverse.NearColumn, reverse.FarCollection, reverse.FarColumn));
    //A task whose parent matches, and a task whose subtasks match.
    Assert.Equal((RelationDirection.ToTarget, "ParentId", Tasks, "Id"),
      (parent.Direction, parent.NearColumn, parent.FarCollection, parent.FarColumn));
    Assert.Equal((RelationDirection.ToSource, "Id", Tasks, "ParentId"),
      (subtasks.Direction, subtasks.NearColumn, subtasks.FarCollection, subtasks.FarColumn));
  }

  //Until step 4.2 this pinned the gap: a relation step was planned and refused at execution. Now
  //the step runs as a semi-join (RL-3a), and a None step over an empty far collection keeps every
  //record, reported as an anti-join scan with the inner query nested.
  [Fact]
  public void APlanWithARelationStepExecutesItAsASemiJoin() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    var conferences = db.Entities<Conference>(Conferences);
    db.InTransaction(() => {
      conferences.Insert(new Conference { Id = 1, City = "Lviv" });
      conferences.Insert(new Conference { Id = 2, City = "Kyiv" });
    });

    var result = conferences.Query().WhereRelated("ExpenseConference", RelationQuantifier.None).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(2, result.Records.Count);
    Assert.Equal("full scan of Conference (the only condition is an anti-join, which no index shape answers)", result.Report.AccessPath);
    var inner = Assert.Single(result.Report.InnerReports);
    Assert.Equal(Expenses, inner.CollectionName);
    Assert.Equal(0, inner.DistinctKeys);
  }

  // =====================================================================
  // The plan and its execution (QM-2, QM-2a, QM-2b).
  // =====================================================================

  [Fact]
  public void ExplainReadsNoDataPageAndThePlanItReturnsRunsAsItStands() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 2_000);
    var entities = db.Entities<Person>(People);
    var query = entities.Query()
      .Where(Compare("Age", ComparisonOperator.GreaterOrEqual, 50))
      .OrderByDescending("Id")
      .Skip(10)
      .Take(20);

    var pagesBefore = db.PageReadCount;
    var plan = query.Explain();
    Assert.Equal(pagesBefore, db.PageReadCount);

    var direct = entities.Run(plan);
    var planned = query.Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(direct.Report.ToString());
    Assert.Equal("index range on Person.Age [GreaterOrEqual 50]", plan.Access.Path.Describe());
    Assert.Equal(plan.Access.Path.Describe(), direct.Report.AccessPath);
    Assert.Equal(planned.Report.AccessPath, direct.Report.AccessPath);
    Assert.Equal(planned.Records.Select(record => record.RecordId), direct.Records.Select(record => record.RecordId));
    //Numbered() gives ages 20 + i % 40, so ages of 50 and over are the last ten of every forty.
    var expected = Enumerable.Range(0, 2_000).Where(i => 20 + i % 40 >= 50).OrderDescending().Skip(10).Take(20);
    Assert.Equal(expected, direct.Records.Select(record => record.Value.Id));
    Assert.Equal(20, direct.Report.DocumentsMaterialised);
  }

  //A plan belongs to its collection. Handed to the entities of another, it would turn one
  //collection's records into another's type.
  [Fact]
  public void APlanIsRefusedByTheEntitiesOfAnotherCollection() {
    using var file = new TempDatabaseFile();
    using var db = NewRelatedDatabase(file);
    var plan = db.Entities<Expense>(Expenses).Query().Explain();

    Assert.Throws<ArgumentException>(() => db.Entities<Conference>(Conferences).Run(plan));
  }

  //N-9 and QM-2a. An index created and an index dropped after the plan was made each move the
  //version, and the plan is refused rather than re-planned behind the caller's back.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void APlanHandedBackAfterTheCatalogueMovedIsRefusedByItsVersion(bool create) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);
    var entities = db.Entities<Person>(People);
    var plan = entities.Query().Where(Compare("Age", ComparisonOperator.Equal, 30)).Explain();
    //Against its own version it runs — inserting records does not move the catalogue.
    Assert.Equal(5, entities.Run(plan).Records.Count);

    if (create) {
      db.CreateIndex(People, "City");
    } else {
      db.DropIndex(People, "Age");
    }
    var refused = Assert.Throws<StalePlanException>(() => entities.Run(plan));

    _output.WriteLine(refused.Message);
    Assert.Equal(plan.CatalogVersion, refused.ExpectedVersion);
    Assert.True(refused.FoundVersion > refused.ExpectedVersion);
    Assert.Contains($"version {refused.ExpectedVersion}", refused.Message);
    Assert.Contains($"version {refused.FoundVersion}", refused.Message);
    //Planning again is the caller's decision, and one call.
    var replanned = entities.Run(plan.Request);
    Assert.Equal(5, replanned.Records.Count);
    Assert.Equal(create ? "index seek on Person.Age" : "full scan of Person (no index on Age)",
      replanned.Report.AccessPath);
  }

  //QM-2b. A schema change started while a query is part-way through its walk waits for the query's
  //lease, rather than creating and dropping an index underneath it.
  [Fact]
  public void ASchemaChangeDuringAQueryWaitsForItsLease() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 300);
    var entities = db.Entities<Person>(People);
    var gate = new GateExpression();

    var query = Task.Run(() => entities.Query()
      .Where(new AndExpression([Compare("Age", ComparisonOperator.GreaterOrEqual, 20), gate]))
      .OrderBy("Id")
      .Run());
    Task change = null;
    try {
      Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(30)), "the query never reached its walk");
      change = Task.Run(() => {
        db.CreateIndex(People, "City");
        db.DropIndex(People, "City");
      });

      Assert.False(change.Wait(TimeSpan.FromMilliseconds(500)), "the schema change ran inside the query's lease");
      Assert.DoesNotContain(db.Indexes, index => index.ColumnName == "City");
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
    //The query neither failed nor saw a half-changed catalogue: every record, in order, through the
    //path it was planned with.
    Assert.Equal(Enumerable.Range(0, 300), result.Records.Select(record => record.Value.Id));
    Assert.Equal("index range on Person.Age [GreaterOrEqual 20]", result.Report.AccessPath);
    Assert.DoesNotContain(db.Indexes, index => index.ColumnName == "City");
  }

  //Holds the query inside its walk, lease and all, until the test lets it go. It is a residual, so it
  //is evaluated per record against the page.
  private sealed class GateExpression : IExpression {
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

  // =====================================================================
  // The page window (QM-4b, QM-5).
  // =====================================================================

  [Fact]
  public void TakeZeroIsAnEmptyPageThatReadsNothingAndStillReports() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);
    var reports = new List<QueryReport>();
    db.Queries.QueryExecuted += reports.Add;

    var pagesBefore = db.PageReadCount;
    var result = db.Entities<Person>(People).Query()
      .Where(Compare("Age", ComparisonOperator.Equal, 30))
      .Take(0)
      .Run();

    Assert.Equal(pagesBefore, db.PageReadCount);
    Assert.Empty(result.Records);
    Assert.Same(result.Report, Assert.Single(reports));
    Assert.Equal("index seek on Person.Age", result.Report.AccessPath);
    Assert.Equal(0, result.Report.PagesRead);
    Assert.Equal(0, result.Report.RecordsExamined);
    Assert.Equal(0, result.Report.DocumentsMaterialised);
  }

  //N-11. The window is held in 64 bits: int.MaxValue twice is a window starting past the end, not a
  //negative one, and it is never allocated.
  [Fact]
  public void AWindowAtTheEdgeOfTheArithmeticBuildsPlansAndReturnsAnEmptyPage() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 200);
    var entities = db.Entities<Person>(People);

    var query = entities.Query().OrderBy("Age").Skip(int.MaxValue).Take(int.MaxValue);
    var request = query.Build();
    var plan = query.Explain();
    var result = entities.Run(plan);

    Assert.Equal(int.MaxValue, request.Skip);
    Assert.Equal(int.MaxValue, request.Take);
    Assert.Equal(2L * int.MaxValue, request.End);
    Assert.Equal(int.MaxValue, plan.Skip);
    Assert.Empty(result.Records);
    Assert.Equal(200, result.Report.RecordsMatched);
    Assert.Equal(0, result.Report.DocumentsMaterialised);

    //At the edge of 64 bits the sum saturates rather than wrapping.
    var edge = entities.Query().OrderByDescending("Age").Skip(long.MaxValue).Take(long.MaxValue);
    Assert.Equal(long.MaxValue, edge.Build().End);
    Assert.Empty(edge.Run().Records);
    //And a Take the size of the address space over an unordered query is every match, not a reservation.
    Assert.Equal(200, entities.Query().Take(long.MaxValue).Run().Records.Count);
  }

  //PG-4, as far as Phase 1 goes: Take without an order is any N matching records.
  [Fact]
  public void TakeWithoutAnOrderReturnsThatManyMatchingRecords() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    Fill(db, 400);

    var result = db.Entities<Person>(People).Query().Where(Compare("Age", ComparisonOperator.Equal, 30)).Take(5).Run();

    Assert.Equal(5, result.Records.Count);
    Assert.All(result.Records, record => Assert.Equal(30, record.Value.Age));
  }

  //Q-4 and OR-1 as Phase 1 applies them: ties are broken by identity, in the direction of the last
  //column, so the pages of an order with many ties partition it.
  [Fact]
  public void PagesOverAnOrderFullOfTiesPartitionIt() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 400);
    var entities = db.Entities<Person>(People);

    foreach (var descending in new[] { false, true }) {
      var ordered = descending ? entities.Query().OrderByDescending("Age") : entities.Query().OrderBy("Age");
      var whole = ordered.Run().Records.Select(record => record.RecordId).ToList();
      var pages = Enumerable.Range(0, 9)
        .SelectMany(page => ordered.Skip(page * 50).Take(50).Run().Records.Select(record => record.RecordId))
        .ToList();

      Assert.Equal(whole, pages);
      //The identity's bytes, which is what its key is.
      var identity = Comparer<Ulid>.Create((left, right) => left.ToByteArray().AsSpan().SequenceCompareTo(right.ToByteArray()));
      var byAge = ids.Select((id, i) => (id, age: 20 + i % 40));
      var expected = descending
        ? byAge.OrderByDescending(pair => pair.age).ThenByDescending(pair => pair.id, identity)
        : byAge.OrderBy(pair => pair.age).ThenBy(pair => pair.id, identity);
      Assert.Equal(expected.Select(pair => pair.id), whole);
    }
  }

  // =====================================================================
  // QM-3: the existing entry point, beside the builder.
  // =====================================================================

  [Fact]
  public void TheBuilderAndTheExistingEntryPointReturnTheSameRecordsForAQueryBothCanExpress() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 400);
    var entities = db.Entities<Person>(People);
    var predicate = new AndExpression([
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
      Compare("Age", ComparisonOperator.LessOrEqual, 34)]);

    var existing = entities.Query(QueryNormalizer.Normalize(predicate));
    var built = entities.Query().Where(predicate).Run();
    //Ages 30 and 31 match; 25 does not.
    var existingById = entities.Query(QueryNormalizer.Normalize(predicate), [ids[10], ids[11], ids[45]]);
    var builtById = entities.Query().Where(predicate).WhereIdIn([ids[10], ids[11], ids[45]]).Run();

    Assert.Equal(existing.Report.AccessPath, built.Report.AccessPath);
    Assert.Equal(existing.Records.Select(record => record.RecordId), built.Records.Select(record => record.RecordId));
    Assert.Equal(existingById.Records.Select(record => record.RecordId), builtById.Records.Select(record => record.RecordId));
    Assert.Equal([ids[10], ids[11]], builtById.Records.Select(record => record.RecordId));
  }

  //Where the two entry points part: the existing one reads an empty id list as no restriction,
  //and a request reads it as what it says.
  [Fact]
  public void AnEmptyIdListIsAQueryForNoRecord() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ids = Fill(db, 100);
    var entities = db.Entities<Person>(People);

    var none = entities.Query().WhereIdIn([]).Run();
    var disjoint = entities.Query().WhereIdIn([ids[1], ids[2]]).WhereIdIn([ids[3]]).Run();
    var both = entities.Query().WhereIdIn([ids[1], ids[2], ids[1]]).WhereIdIn([ids[2], ids[3]]).Run();

    Assert.Empty(none.Records);
    Assert.Equal(0, none.Report.RecordsExamined);
    Assert.Empty(disjoint.Records);
    Assert.Equal(ids[2], Assert.Single(both.Records).RecordId);
  }
}
