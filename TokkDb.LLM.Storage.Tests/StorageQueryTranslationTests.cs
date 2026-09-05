using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;
using TokkDb.Values;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// DC-5: one query representation. Every query shape <see cref="RecordQueryTests"/> runs is
/// bound the way that file binds it, and then translated into the engine's expression tree
/// and normalised — so that what the application asks for and what the engine plans over
/// are the same query and not two of them.
/// </summary>
public sealed class StorageQueryTranslationTests
{
    private static readonly RecordQueryBinder Binder = new();

    /// <summary>The schema of RecordQueryTests.BuildShop, without the records: translation
    /// is about the query, and no data is read to perform it.</summary>
    private static MemoryStorage BuildShopSchema()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("Customer", "Customers", new[]
        {
            new ColumnDefinition("CustomerId", ColumnType.String, unique: true),
            new ColumnDefinition("FullName", ColumnType.String),
            new ColumnDefinition("Phone", ColumnType.String),
            new ColumnDefinition("Age", ColumnType.Int32)
        }));
        storage.CreateCollection(new CollectionDefinition("Order", "Orders", new[]
        {
            new ColumnDefinition("OrderId", ColumnType.String, unique: true),
            new ColumnDefinition("CustomerId", ColumnType.String),
            new ColumnDefinition("Sku", ColumnType.String)
        }));
        storage.CreateCollection(new CollectionDefinition("Product", "Products", new[]
        {
            new ColumnDefinition("Sku", ColumnType.String, unique: true),
            new ColumnDefinition("Name", ColumnType.String),
            new ColumnDefinition("Price", ColumnType.Decimal)
        }));
        storage.AddRelation(new RelationDefinition(
            "CustomerOrders", RelationType.OneToMany, "Customer", "CustomerId", "Order", "CustomerId"));
        storage.AddRelation(new RelationDefinition(
            "OrderProduct", RelationType.ManyToOne, "Order", "Sku", "Product", "Sku"));
        return storage;
    }

    private static StorageQueryTranslator.TranslatedQuery Translate(MemoryStorage storage, RecordQuery query) =>
        StorageQueryTranslator.Translate(Binder.Bind(storage, query));

    /// <summary>
    /// The queries of RecordQueryTests, named after the test each one comes from. The
    /// rejected ones are not here: a query the binder refuses never reaches translation.
    /// </summary>
    public static TheoryData<string, RecordQuery> ShopQueries() => new()
    {
        { "FindsCustomersWithUkrainianPhoneNumbers", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" },
            OrderBy = [new RecordQuerySort { Column = "FullName", Direction = "asc" }] } },
        { "FindsCustomersWhoBoughtAProductCostingFortyOrMore", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = "any",
                Where = new RecordFilter { Relation = "OrderProduct", Quantifier = "any",
                    Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "40" } } },
            OrderBy = [new RecordQuerySort { Column = "FullName" }] } },
        { "CombinesConditionsWithAndOr (and)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "and", Filters = [
                new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" },
                new RecordFilter { Field = "Age", Operator = "gte", Value = "30" }] },
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "desc" }] } },
        { "CombinesConditionsWithAndOr (or)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "or", Filters = [
                new RecordFilter { Field = "FullName", Operator = "eq", Value = "Olena" },
                new RecordFilter { Field = "FullName", Operator = "eq", Value = "John" }] } } },
        { "NegatesWithNot", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "not", Filters = [
                new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" }] } } },
        { "SupportsBetweenAndIn (between)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Age", Operator = "between", Values = ["30", "45"] },
            OrderBy = [new RecordQuerySort { Column = "Age" }] } },
        { "SupportsBetweenAndIn (in)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "FullName", Operator = "in", Values = ["Olena", "John"] },
            OrderBy = [new RecordQuerySort { Column = "FullName" }] } },
        { "SkipAndTakeSliceTheOrderedResult", new RecordQuery {
            CollectionName = "Customer",
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "asc" }], Skip = 1, Take = 2 } },
        { "AnUnfilteredQueryIsCappedAtTenRecords", new RecordQuery { CollectionName = "Customer" } },
        { "SortsNumericallyNotAlphabetically", new RecordQuery {
            CollectionName = "Customer",
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "asc" }] } },
        { "SelectReturnsOnlyRequestedColumns", new RecordQuery {
            CollectionName = "Customer", Select = ["FullName", "Age"] } },
        { "RelationQuantifiersNoneAndAllAreSupported (none)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = "none",
                Where = new RecordFilter { Field = "Sku", Operator = "eq", Value = "p-cheap" } } } },
        { "RelationQuantifiersNoneAndAllAreSupported (all)", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = "all",
                Where = new RecordFilter { Field = "Sku", Operator = "eq", Value = "p-cheap" } } } },
        { "TraversesARelationDeclaredInTheOppositeDirection", new RecordQuery {
            CollectionName = "Order",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = "any",
                Where = new RecordFilter { Field = "FullName", Operator = "eq", Value = "Olena" } } } },
        { "NestedColumnIsResolvedAgainstTheRelatedCollection", new RecordQuery {
            CollectionName = "Order",
            Where = new RecordFilter { Relation = "OrderProduct", Quantifier = "any",
                Where = new RecordFilter { Field = "Price", Operator = "lte", Value = "12" } } } },
        { "EmptyResultIsNotAnError", new RecordQuery {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "FullName", Operator = "eq", Value = "Nobody" } } }
    };

    /// <summary>
    /// The done-when: every one of them translates and normalises. Nothing here executes,
    /// which is the point — the shape of a query is settled before any record is read.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShopQueries))]
    public void EveryQueryOfRecordQueryTestsTranslatesAndNormalises(string source, RecordQuery query)
    {
        var storage = BuildShopSchema();

        var translated = Translate(storage, query);

        Assert.False(string.IsNullOrEmpty(translated.CollectionName));
        Assert.NotNull(translated.Normalized);
        //The split covers the predicate: a query with a filter yields conjuncts, a residual,
        //or both, and never nothing.
        if (query.Where is not null)
        {
            Assert.False(translated.Normalized.IsEverything,
                $"{source} had a filter that normalised to no constraint at all");
        }
        else
        {
            Assert.True(translated.Normalized.IsEverything, source);
        }
    }

    /// <summary>The record ids are not a filter, because identity is not a column. They ride
    /// alongside the predicate rather than inside it.</summary>
    [Fact]
    public void RecordIdsAreCarriedBesideThePredicateRatherThanTranslatedIntoIt()
    {
        var storage = BuildShopSchema();
        var identity = Ulid.NewUlid();

        var translated = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            RecordIds = [identity.ToString()],
            Where = new RecordFilter { Field = "Age", Operator = "gte", Value = "30" }
        });

        Assert.Equal([identity], translated.Ids);
        Assert.Single(translated.Normalized.Conjuncts);
    }

    [Fact]
    public void AComparisonBecomesOneConjunctNamingTheColumnTheOperatorAndTheConstant()
    {
        var storage = BuildShopSchema();

        var normalized = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" }
        }).Normalized;

        var conjunct = Assert.Single(normalized.Conjuncts);
        Assert.Equal("Phone", conjunct.ColumnName);
        Assert.Equal(ComparisonOperator.StartsWith, conjunct.Operator);
        Assert.Equal(ValueTypeEnum.String, conjunct.ColumnType);
        Assert.Equal("+380", Assert.IsType<StringDocumentValue>(conjunct.Constant).Value);
        Assert.True(normalized.IsFullyNormalized);
    }

    /// <summary>
    /// "between" is two comparisons, so it normalises into two conjuncts and an index can be
    /// opened at either end of the range rather than only scanned from one.
    /// </summary>
    [Fact]
    public void BetweenBecomesTwoConjunctsRatherThanOneOperatorTheEngineHasToKnow()
    {
        var storage = BuildShopSchema();

        var normalized = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Age", Operator = "between", Values = ["30", "45"] }
        }).Normalized;

        Assert.Equal(2, normalized.Conjuncts.Count);
        Assert.All(normalized.Conjuncts, conjunct => Assert.Equal("Age", conjunct.ColumnName));
        Assert.Equal(ComparisonOperator.GreaterOrEqual, normalized.Conjuncts[0].Operator);
        Assert.Equal(ComparisonOperator.LessOrEqual, normalized.Conjuncts[1].Operator);
        Assert.Equal(30, Assert.IsType<IntDocumentValue>(normalized.Conjuncts[0].Constant).Value);
        Assert.Equal(45, Assert.IsType<IntDocumentValue>(normalized.Conjuncts[1].Constant).Value);
        Assert.True(normalized.IsFullyNormalized);
    }

    [Fact]
    public void AnInKeepsItsOperandsTogetherInOneIndexableConjunct()
    {
        var storage = BuildShopSchema();

        var conjunct = Assert.Single(Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "FullName", Operator = "in", Values = ["Olena", "John"] }
        }).Normalized.Conjuncts);

        Assert.Equal(ComparisonOperator.In, conjunct.Operator);
        Assert.Equal(["Olena", "John"],
            conjunct.Constants.Select(value => ((StringDocumentValue)value).Value));
        Assert.True(conjunct.IsIndexable);
    }

    [Fact]
    public void AnAndYieldsAConjunctPerComparisonAndNoResidual()
    {
        var storage = BuildShopSchema();

        var normalized = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "and", Filters = [
                new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" },
                new RecordFilter { Field = "Age", Operator = "gte", Value = "30" }] }
        }).Normalized;

        Assert.Equal(["Phone", "Age"], normalized.Conjuncts.Select(conjunct => conjunct.ColumnName));
        Assert.True(normalized.IsFullyNormalized);
    }

    [Fact]
    public void AnOrAndANotStayWholeInTheResidual()
    {
        var storage = BuildShopSchema();

        var or = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "or", Filters = [
                new RecordFilter { Field = "FullName", Operator = "eq", Value = "Olena" },
                new RecordFilter { Field = "FullName", Operator = "eq", Value = "John" }] }
        }).Normalized;
        Assert.Empty(or.Conjuncts);
        Assert.IsType<OrExpression>(or.Residual);

        var not = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Logic = "not", Filters = [
                new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" }] }
        }).Normalized;
        Assert.Empty(not.Conjuncts);
        Assert.IsType<NotExpression>(not.Residual);
    }

    /// <summary>
    /// A relation step reaches into another collection, so it is never a conjunct. It keeps
    /// what the planner needs to run it — the relation, the columns and the quantifier — and
    /// the predicate on the far side comes with it.
    /// </summary>
    [Fact]
    public void ARelationStepIsResidualAndCarriesWhatThePlannerNeedsToRunIt()
    {
        var storage = BuildShopSchema();

        var normalized = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = "any",
                Where = new RecordFilter { Relation = "OrderProduct", Quantifier = "any",
                    Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "40" } } }
        }).Normalized;

        Assert.Empty(normalized.Conjuncts);
        var step = Assert.IsType<RelationStepExpression>(normalized.Residual);
        Assert.Equal("CustomerOrders", step.RelationName);
        Assert.Equal("Order", step.TargetCollection);
        Assert.Equal("CustomerId", step.TargetColumn);
        Assert.Equal(RelationQuantifier.Any, step.Quantifier);

        var inner = Assert.IsType<RelationStepExpression>(step.Inner);
        Assert.Equal("Product", inner.TargetCollection);
        Assert.Equal(ValueTypeEnum.Decimal,
            Assert.IsType<ComparisonExpression>(inner.Inner).ColumnType);
    }

    [Theory]
    [InlineData("none", RelationQuantifier.None)]
    [InlineData("all", RelationQuantifier.All)]
    [InlineData("any", RelationQuantifier.Any)]
    public void EveryQuantifierSurvivesTheTranslation(string quantifier, RelationQuantifier expected)
    {
        var storage = BuildShopSchema();

        var normalized = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Relation = "CustomerOrders", Quantifier = quantifier,
                Where = new RecordFilter { Field = "Sku", Operator = "eq", Value = "p-cheap" } }
        }).Normalized;

        Assert.Equal(expected, Assert.IsType<RelationStepExpression>(normalized.Residual).Quantifier);
    }

    /// <summary>
    /// The Phase 4 finding reaching the planner instead of surprising it. Decimal has no
    /// document value of its own and is stored as invariant text, and text does not order the
    /// way a number does. The conjunct is still a conjunct — the planner has to check it —
    /// but it says it cannot be turned into an index range.
    /// </summary>
    [Fact]
    public void AnOrderedComparisonOverADecimalIsAConjunctThatCannotBecomeAnIndexRange()
    {
        var storage = BuildShopSchema();

        var ordered = Assert.Single(Translate(storage, new RecordQuery
        {
            CollectionName = "Product",
            Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "40" }
        }).Normalized.Conjuncts);
        Assert.Equal(ValueTypeEnum.Decimal, ordered.ColumnType);
        Assert.False(ordered.IsIndexable);

        var equality = Assert.Single(Translate(storage, new RecordQuery
        {
            CollectionName = "Product",
            Where = new RecordFilter { Field = "Price", Operator = "eq", Value = "40" }
        }).Normalized.Conjuncts);
        Assert.True(equality.IsIndexable);
    }

    /// <summary>The parts of a query that are not a predicate are carried through untouched;
    /// normalising a filter must not quietly drop the paging or the ordering.</summary>
    [Fact]
    public void OrderingPagingAndProjectionRideThroughUnchanged()
    {
        var storage = BuildShopSchema();

        var translated = Translate(storage, new RecordQuery
        {
            CollectionName = "Customer",
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "desc" }],
            Skip = 1,
            Take = 2,
            Select = ["FullName", "Age"]
        });

        Assert.Equal("Customer", translated.CollectionName);
        Assert.Equal([("Age", true)], translated.OrderBy);
        Assert.Equal(1, translated.Skip);
        Assert.Equal(2, translated.Take);
        Assert.Equal(["FullName", "Age"], translated.Select);
        Assert.Null(translated.Where);
        Assert.True(translated.Normalized.IsEverything);
    }
}
