using System.Collections.Immutable;
using System.Reflection;
using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//QM-1, QM-1a and Q-1: the request is immutable to the bottom of every collection it owns, the
//builder is a persistent value, and NormalizedQuery is left exactly as it was.
public class QueryRequestTests {
  private const string Collection = nameof(Person);

  private static IExpression Column(string name) {
    return new PropertyExpression(name) { Parent = new RootExpression() };
  }

  private static ComparisonExpression Compare(string column, ComparisonOperator op, int value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new IntDocumentValue(value)),
      ValueTypeEnum.Int);
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("Age", ValueTypeEnum.Int)
    ]);
    return db;
  }

  // =====================================================================
  // No mutable member, asserted on the types rather than on one instance.
  // =====================================================================

  //The types a request is made of. Anything else a member holds has to be a value that cannot
  //change, or the assertion fails naming it.
  private static readonly HashSet<Type> RequestTypes = [typeof(QueryRequest), typeof(OrderColumn), typeof(RelationStep)];

  [Fact]
  public void TheRequestExposesNoMutableMember() {
    AssertImmutable(typeof(QueryRequest), []);
  }

  private static void AssertImmutable(Type type, HashSet<Type> seen) {
    if (!seen.Add(type)) {
      return;
    }
    //Sealed, or a subclass could add state the reflection below never sees.
    Assert.True(type.IsSealed, $"{type.Name} is not sealed");
    const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
      BindingFlags.DeclaredOnly;
    foreach (var field in type.GetFields(members)) {
      Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} is a field that can be assigned");
      AssertImmutableValue(type, field.Name, field.FieldType, seen);
    }
    foreach (var property in type.GetProperties(members)) {
      //No init accessor either: a with-expression could otherwise make a request that never
      //passed through the constructor's refusals.
      Assert.True(property.SetMethod is null, $"{type.Name}.{property.Name} has a setter");
      AssertImmutableValue(type, property.Name, property.PropertyType, seen);
    }
  }

  private static void AssertImmutableValue(Type owner, string member, Type declared, HashSet<Type> seen) {
    var type = Nullable.GetUnderlyingType(declared) ?? declared;
    if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Ulid) || type == typeof(Type)) {
      return;
    }
    //An immutable array and nothing weaker: IReadOnlyList would be satisfied by a list someone
    //else still holds.
    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)) {
      AssertImmutableValue(owner, member, type.GetGenericArguments()[0], seen);
      return;
    }
    //The document layer's type, left as it is (Q-1). What the request puts in it is checked on an
    //instance in TheConjunctsAndTheirConstantsAreImmutableArrays.
    if (type == typeof(NormalizedQuery)) {
      return;
    }
    Assert.True(RequestTypes.Contains(type), $"{owner.Name}.{member} is a {type.Name}, which is not known to be immutable");
    AssertImmutable(type, seen);
  }

  [Fact]
  public void TheConjunctsAndTheirConstantsAreImmutableArrays() {
    var constants = new List<IDocumentValue> { new IntDocumentValue(30), new IntDocumentValue(31) };
    var query = new NormalizedQuery(
      [new QueryPredicate("Age", ComparisonOperator.In, ValueTypeEnum.Int, constants)], null);

    var request = new QueryRequest(query);

    var conjunct = Assert.Single(request.Query.Conjuncts);
    Assert.IsType<ImmutableArray<QueryPredicate>>(request.Query.Conjuncts);
    Assert.IsType<ImmutableArray<IDocumentValue>>(conjunct.Constants);
    Assert.Throws<NotSupportedException>(() => ((IList<IDocumentValue>)conjunct.Constants).Add(new IntDocumentValue(32)));
  }

  // =====================================================================
  // Copied, not wrapped: what the caller goes on doing to its lists changes nothing.
  // =====================================================================

  [Fact]
  public void ChangingTheCollectionsPassedToTheRequestLeavesTheRequestAsItWas() {
    var constants = new List<IDocumentValue> { new IntDocumentValue(30) };
    var conjuncts = new List<QueryPredicate> { new("Age", ComparisonOperator.In, ValueTypeEnum.Int, constants) };
    var ids = new List<Ulid> { Ulid.NewUlid() };
    var order = new List<OrderColumn> { new("Age") };
    var steps = new List<RelationStep> { new("PersonTeam", RelationQuantifier.Any) };

    var request = new QueryRequest(new NormalizedQuery(conjuncts, null), ids, order, 0, 5, steps);
    var described = request.ToString();

    constants.Add(new IntDocumentValue(31));
    conjuncts.Add(new QueryPredicate("Name", ComparisonOperator.Equal, ValueTypeEnum.String,
      [new StringDocumentValue("x")]));
    ids.Add(Ulid.NewUlid());
    order.Add(new OrderColumn("Name"));
    steps.Add(new RelationStep("PersonCity", RelationQuantifier.None));

    Assert.Single(Assert.Single(request.Query.Conjuncts).Constants);
    Assert.Single(request.Ids!.Value);
    Assert.Equal("Age", Assert.Single(request.Order).ColumnName);
    Assert.Equal("PersonTeam", Assert.Single(request.RelationSteps).RelationName);
    Assert.Equal(described, request.ToString());
  }

  //QM-1a: the builder is a value, so there is no "mutate it after building" left to do to it —
  //what can still change is what was handed to it.
  [Fact]
  public void ChangingTheInputsOfTheBuilderLeavesTheQueryAndItsRequestAsTheyWere() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var ages = new List<IDocumentValue> { new IntDocumentValue(30) };
    var ids = new List<Ulid> { Ulid.NewUlid() };

    var query = db.Entities<Person>(Collection).Query()
      .Where(new ComparisonExpression(Column("Age"), ComparisonOperator.In, new ConstantExpression(ages),
        ValueTypeEnum.Int))
      .WhereIdIn(ids)
      .WhereRelated("PersonTeam", RelationQuantifier.Any,
        new ComparisonExpression(Column("Size"), ComparisonOperator.In, new ConstantExpression(ages), ValueTypeEnum.Int));
    var built = query.Build();

    ages.Add(new IntDocumentValue(31));
    ids.Add(Ulid.NewUlid());

    foreach (var request in new[] { built, query.Build() }) {
      Assert.Single(Assert.Single(request.Query.Conjuncts).Constants);
      Assert.Single(request.Ids!.Value);
      Assert.Single(Assert.Single(Assert.Single(request.RelationSteps).Inner.Conjuncts).Constants);
    }
  }

  [Fact]
  public void ExtendingTheBuilderAfterBuildingChangesNothingInWhatWasBuilt() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var baseQuery = db.Entities<Person>(Collection).Query().Where(Compare("Age", ComparisonOperator.Greater, 20));
    var built = baseQuery.Build();

    var ordered = baseQuery.OrderBy("Age").ThenByDescending("Name").Skip(10);
    var capped = baseQuery.Take(5).Where(Compare("Id", ComparisonOperator.Less, 100))
      .WhereRelated("PersonTeam", RelationQuantifier.None);

    //What was built before either extension is untouched by both.
    Assert.Single(built.Query.Conjuncts);
    Assert.Empty(built.Order);
    Assert.Equal(0, built.Skip);
    Assert.Null(built.Take);
    Assert.Empty(built.RelationSteps);

    //And a base extended two ways gives two requests, neither carrying the other's additions.
    var orderedRequest = ordered.Build();
    var cappedRequest = capped.Build();
    Assert.Equal(["Age", "Name desc"], orderedRequest.Order.Select(column => column.ToString()));
    Assert.Equal(10, orderedRequest.Skip);
    Assert.Null(orderedRequest.Take);
    Assert.Single(orderedRequest.Query.Conjuncts);
    Assert.Empty(orderedRequest.RelationSteps);
    Assert.Empty(cappedRequest.Order);
    Assert.Equal(0, cappedRequest.Skip);
    Assert.Equal(5, cappedRequest.Take);
    Assert.Equal(2, cappedRequest.Query.Conjuncts.Count);
    Assert.Single(cappedRequest.RelationSteps);
  }

  // =====================================================================
  // NormalizedQuery stays what it is.
  // =====================================================================

  //The relation step is lifted out of a copy. The query the caller normalised still has it in its
  //residual, and its conjunct list is the same list.
  [Fact]
  public void LiftingARelationStepLeavesTheNormalizedQueryItCameFromUntouched() {
    var step = new RelationStepExpression("PersonTeam", "TeamId", "Team", "Id", RelationQuantifier.Any, null);
    var normalized = QueryNormalizer.Normalize(new AndExpression([Compare("Age", ComparisonOperator.Equal, 30), step]));
    var conjuncts = normalized.Conjuncts;

    var request = new QueryRequest(normalized);

    Assert.Same(step, normalized.Residual);
    Assert.Same(conjuncts, normalized.Conjuncts);
    Assert.Single(normalized.Conjuncts);
    Assert.Null(request.Query.Residual);
    Assert.Equal("PersonTeam", Assert.Single(request.RelationSteps).RelationName);
  }

  //QM-4b: the end of the page is Skip + Take in 64 bits, and saturates instead of wrapping.
  [Theory]
  [InlineData(0L, null, long.MaxValue)]
  [InlineData(20L, 20L, 40L)]
  [InlineData((long)int.MaxValue, (long)int.MaxValue, 2L * int.MaxValue)]
  [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
  [InlineData(long.MaxValue - 1, 1L, long.MaxValue)]
  [InlineData(long.MaxValue - 1, 2L, long.MaxValue)]
  public void TheEndOfThePageIsHeldIn64BitsAndSaturates(long skip, long? take, long end) {
    var request = new QueryRequest(order: [new OrderColumn("Age")], skip: skip, take: take);

    Assert.Equal(end, request.End);
  }
}
