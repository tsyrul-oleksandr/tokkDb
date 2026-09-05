using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//DC-5. The normalised form on its own, built by hand and checked without a database: what
//the planner will be handed, and the rule about which parts of a predicate can be lifted out
//of it and which cannot.
public class QueryNormalizerTests {
  private static IExpression Column(string name) {
    return new PropertyExpression(name) { Parent = new RootExpression() };
  }

  private static ComparisonExpression Compare(string column, ComparisonOperator op, int value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new IntDocumentValue(value)),
      ValueTypeEnum.Int);
  }

  private static ComparisonExpression Compare(string column, ComparisonOperator op, string value,
      ValueTypeEnum type = ValueTypeEnum.String) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new StringDocumentValue(value)),
      type);
  }

  [Fact]
  public void NoPredicateAtAllIsEverything() {
    var normalized = QueryNormalizer.Normalize(null);
    Assert.True(normalized.IsEverything);
    Assert.True(normalized.IsFullyNormalized);
    Assert.Empty(normalized.Conjuncts);
  }

  [Fact]
  public void OneComparisonIsOneConjunctAndNoResidual() {
    var normalized = QueryNormalizer.Normalize(Compare("Age", ComparisonOperator.GreaterOrEqual, 30));

    var conjunct = Assert.Single(normalized.Conjuncts);
    Assert.Equal("Age", conjunct.ColumnName);
    Assert.Equal(ComparisonOperator.GreaterOrEqual, conjunct.Operator);
    Assert.Equal(ValueTypeEnum.Int, conjunct.ColumnType);
    Assert.Equal(30, Assert.IsType<IntDocumentValue>(conjunct.Constant).Value);
    Assert.True(normalized.IsFullyNormalized);
  }

  //A binder nests its ANDs and a caller writing by hand does too. One AND of three is the
  //same predicate as three ANDs of two, and the planner should not have to know which it got.
  [Fact]
  public void NestedAndsFlattenIntoOneConjunction() {
    var expression = new AndExpression([
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
      new AndExpression([
        Compare("Phone", ComparisonOperator.StartsWith, "+380"),
        new AndExpression([Compare("FullName", ComparisonOperator.Equal, "Olena")])
      ])
    ]);

    var normalized = QueryNormalizer.Normalize(expression);

    Assert.Equal(3, normalized.Conjuncts.Count);
    Assert.Equal(["Age", "Phone", "FullName"], normalized.Conjuncts.Select(c => c.ColumnName));
    Assert.True(normalized.IsFullyNormalized);
  }

  //The rule the whole split rests on. Under an OR neither side is required on its own, so
  //lifting one out would narrow the search to records that need not satisfy it — the query
  //would lose rows. The OR therefore stays whole.
  [Fact]
  public void AnOrCannotBeLiftedAndStaysWholeInTheResidual() {
    var expression = new OrExpression([
      Compare("Age", ComparisonOperator.Less, 30),
      Compare("Age", ComparisonOperator.Greater, 50)
    ]);

    var normalized = QueryNormalizer.Normalize(expression);

    Assert.Empty(normalized.Conjuncts);
    Assert.IsType<OrExpression>(normalized.Residual);
    Assert.False(normalized.IsFullyNormalized);
  }

  //And the case that makes the split worth doing: the AND around an OR still yields what it
  //can, so the planner narrows by Age and re-checks the rest.
  [Fact]
  public void AnAndAroundAnOrKeepsWhatItCanAndLeavesTheRest() {
    var expression = new AndExpression([
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
      new OrExpression([
        Compare("FullName", ComparisonOperator.Equal, "Olena"),
        Compare("FullName", ComparisonOperator.Equal, "John")
      ])
    ]);

    var normalized = QueryNormalizer.Normalize(expression);

    var conjunct = Assert.Single(normalized.Conjuncts);
    Assert.Equal("Age", conjunct.ColumnName);
    Assert.IsType<OrExpression>(normalized.Residual);
  }

  //A NOT constrains nothing by itself either: "not Age >= 30" is not a range over Age that
  //an index could be opened at.
  [Fact]
  public void ANegationStaysInTheResidual() {
    var normalized = QueryNormalizer.Normalize(
      new NotExpression(Compare("Phone", ComparisonOperator.StartsWith, "+380")));

    Assert.Empty(normalized.Conjuncts);
    Assert.IsType<NotExpression>(normalized.Residual);
  }

  //Two residuals are conjoined rather than the first one winning.
  [Fact]
  public void SeveralResidualsAreConjoinedTogether() {
    var normalized = QueryNormalizer.Normalize(new AndExpression([
      new NotExpression(Compare("Phone", ComparisonOperator.StartsWith, "+380")),
      Compare("Age", ComparisonOperator.Greater, 20),
      new OrExpression([Compare("Age", ComparisonOperator.Less, 5)])
    ]));

    Assert.Single(normalized.Conjuncts);
    Assert.Equal(2, Assert.IsType<AndExpression>(normalized.Residual).Operands.Count);
  }

  //Written the other way round, which a caller may well do, is the same predicate.
  [Fact]
  public void AConstantOnTheLeftTurnsTheOperatorAbout() {
    var expression = new ComparisonExpression(new ConstantExpression(new IntDocumentValue(40)),
      ComparisonOperator.LessOrEqual, Column("Age"), ValueTypeEnum.Int);

    var conjunct = Assert.Single(QueryNormalizer.Normalize(expression).Conjuncts);

    Assert.Equal("Age", conjunct.ColumnName);
    //40 <= Age is Age >= 40.
    Assert.Equal(ComparisonOperator.GreaterOrEqual, conjunct.Operator);
    Assert.Equal(40, Assert.IsType<IntDocumentValue>(conjunct.Constant).Value);
  }

  //A path that goes deeper than one property names no column of the collection, so there is
  //no index it could be matched against. It is a predicate the planner has to evaluate.
  [Fact]
  public void APathIntoADocumentIsNotAColumnAndStaysInTheResidual() {
    var nested = new PropertyExpression("Code") {
      Parent = new PropertyExpression("Passport") { Parent = new RootExpression() }
    };
    var expression = new ComparisonExpression(nested, ComparisonOperator.Equal,
      new ConstantExpression(new StringDocumentValue("ST-1")), ValueTypeEnum.String);

    var normalized = QueryNormalizer.Normalize(expression);

    Assert.Empty(normalized.Conjuncts);
    Assert.Same(expression, normalized.Residual);
  }

  //Neither side constant: nothing to open an index at.
  [Fact]
  public void AComparisonOfTwoColumnsStaysInTheResidual() {
    var expression = new ComparisonExpression(Column("Age"), ComparisonOperator.Equal,
      Column("Year"), ValueTypeEnum.Int);

    Assert.Empty(QueryNormalizer.Normalize(expression).Conjuncts);
    Assert.Same(expression, QueryNormalizer.Normalize(expression).Residual);
  }

  //A relation step reaches into another collection, which one document cannot answer.
  [Fact]
  public void ARelationStepIsAlwaysResidual() {
    var expression = new AndExpression([
      Compare("Age", ComparisonOperator.Greater, 20),
      new RelationStepExpression("CustomerOrders", "CustomerId", "Order", "CustomerId",
        RelationQuantifier.Any, Compare("Sku", ComparisonOperator.Equal, "p-cheap"))
    ]);

    var normalized = QueryNormalizer.Normalize(expression);

    Assert.Single(normalized.Conjuncts);
    Assert.IsType<RelationStepExpression>(normalized.Residual);
  }

  //An "in" keeps all its operands in one conjunct rather than becoming an OR of equalities,
  //because a set of equalities is a shape an index can answer from and an OR is not.
  [Fact]
  public void AnInKeepsItsWholeOperandListInOneConjunct() {
    var expression = new ComparisonExpression(Column("FullName"), ComparisonOperator.In,
      new ConstantExpression([new StringDocumentValue("Olena"), new StringDocumentValue("John")]),
      ValueTypeEnum.String);

    var conjunct = Assert.Single(QueryNormalizer.Normalize(expression).Conjuncts);

    Assert.Equal(ComparisonOperator.In, conjunct.Operator);
    Assert.Equal(2, conjunct.Constants.Count);
    Assert.True(conjunct.IsIndexable);
  }

  //The Phase 4 finding, carried into the planner rather than left to surprise it. Decimal,
  //Int64, DateTime and Guid have no document value of their own and are stored as invariant
  //text, and text does not order the way a number does: "250" is below "40" as a string.
  //So an ordered comparison over one of them cannot become an index range, even though it is
  //a perfectly good conjunct that the planner must still check.
  [Fact]
  public void AnOrderedComparisonOverATextEncodedTypeIsAConjunctButNotAnIndexableOne() {
    var ordered = Compare("Price", ComparisonOperator.GreaterOrEqual, "40", ValueTypeEnum.Decimal);
    var equality = Compare("Price", ComparisonOperator.Equal, "40", ValueTypeEnum.Decimal);

    Assert.False(Assert.Single(QueryNormalizer.Normalize(ordered).Conjuncts).IsIndexable);
    //Equality still is: an exact match on the stored text is an exact match on the value.
    Assert.True(Assert.Single(QueryNormalizer.Normalize(equality).Conjuncts).IsIndexable);
    //And a type the format does hold is indexable either way.
    Assert.True(Assert.Single(QueryNormalizer.Normalize(
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30)).Conjuncts).IsIndexable);
  }

  //The split has to be exact: conjuncts AND residual is the predicate that came in. Checked
  //by evaluating both against the same records and getting the same answer.
  [Fact]
  public void TheSplitMeansTheSameThingAsThePredicateItCameFrom() {
    var expression = new AndExpression([
      Compare("Age", ComparisonOperator.GreaterOrEqual, 30),
      new OrExpression([
        Compare("FullName", ComparisonOperator.Equal, "Olena"),
        Compare("FullName", ComparisonOperator.Equal, "Marta")
      ]),
      new NotExpression(Compare("Phone", ComparisonOperator.StartsWith, "+1"))
    ]);
    var normalized = QueryNormalizer.Normalize(expression);

    foreach (var (name, age, phone) in new[] {
      ("Olena", 30, "+380671112233"), ("John", 45, "+14155550123"),
      ("Andriy", 25, "+380509998877"), ("Marta", 51, "+380631234567")
    }) {
      var record = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
        ["FullName"] = new StringDocumentValue(name),
        ["Age"] = new IntDocumentValue(age),
        ["Phone"] = new StringDocumentValue(phone)
      });
      Assert.Equal(BooleanExpression.IsTrue(expression.Execute(record, record)),
        Satisfies(normalized, record));
    }
  }

  private static bool Satisfies(NormalizedQuery normalized, IDocumentValue record) {
    foreach (var conjunct in normalized.Conjuncts) {
      var comparison = new ComparisonExpression(Column(conjunct.ColumnName), conjunct.Operator,
        new ConstantExpression(conjunct.Constants), conjunct.ColumnType);
      if (!BooleanExpression.IsTrue(comparison.Execute(record, record))) {
        return false;
      }
    }
    return normalized.Residual is null || BooleanExpression.IsTrue(normalized.Residual.Execute(record, record));
  }
}
