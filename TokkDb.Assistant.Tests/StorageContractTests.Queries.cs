using TokkDb.Assistant.Storage;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The rest of the query model (SC-7, SC-9, D-16): projection, cursor paging, aggregation, and
/// asking about many values at once.
///
/// Shared, because every one of these is a promise about what comes back rather than about how it
/// was reached - and what comes back has to be the same from both implementations. How it was
/// reached is <see cref="QueryExecutionInfo"/>, which each reports honestly for itself.
/// </summary>
public abstract partial class StorageContractTests
{
    private IStorage GivenNumbered(int howMany, string collection = "readings")
    {
        var storage = Storage;

        storage.CreateCollection(new CollectionDefinition(collection, columns:
        [
            new ColumnDefinition("label", ColumnType.Text, required: true),
            new ColumnDefinition("day", ColumnType.Date),
            new ColumnDefinition("value", ColumnType.Integer),
            new ColumnDefinition("cost", ColumnType.Decimal)
        ]));

        storage.InUnitOfWork(() =>
        {
            foreach (var i in Enumerable.Range(0, howMany))
            {
                storage.Create(collection, new Dictionary<string, object?>
                {
                    ["label"] = $"reading {i:0000}",
                    ["day"] = new DateOnly(2026, 1, 1).AddDays(i % 7),
                    ["value"] = (long)i,
                    ["cost"] = 10m + i
                });
            }
        });

        return storage;
    }

    // ---- Projection ---------------------------------------------------------------------------

    [Fact]
    public void A_query_can_ask_for_only_the_columns_it_needs()
    {
        var storage = GivenNumbered(3);

        var result = storage.ExecuteQuery(
            new StorageQuery("readings", orderBy: [new QuerySort("value")]).Selecting("label", "value"));

        var first = result.Records[0];

        Assert.Equal(["label", "value"], first.Fields.Keys.Order());
        Assert.Equal("reading 0000", first["label"]);
        Assert.Equal(0L, first["value"]);

        // A column left out of a projection is left out, not emptied: the same distinction a
        // record keeps everywhere else between absent and nothing.
        Assert.False(first.Has("cost"));
    }

    [Fact]
    public void A_projection_naming_a_column_the_collection_does_not_have_is_refused()
    {
        var storage = GivenNumbered(1);

        var thrown = Assert.Throws<StorageValidationException>(() =>
            storage.ExecuteQuery(new StorageQuery("readings").Selecting("nonsense")));

        Assert.Equal("nonsense", Assert.Single(thrown.Errors.OfType<UnknownColumn>()).ColumnName);
    }

    // ---- Cursor paging (BR-3) ------------------------------------------------------------------

    [Fact]
    public void A_page_continues_from_where_the_last_one_ended()
    {
        var storage = GivenNumbered(10);

        var first = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 4));

        Assert.Equal([0L, 1L, 2L, 3L], first.Records.Select(static record => record["value"]));
        Assert.True(first.HasMore);

        var second = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 4).Continuing(first.NextCursor));

        Assert.Equal([4L, 5L, 6L, 7L], second.Records.Select(static record => record["value"]));

        var third = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 4).Continuing(second.NextCursor));

        Assert.Equal([8L, 9L], third.Records.Select(static record => record["value"]));
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public void A_cursor_survives_being_written_down_and_read_back()
    {
        var storage = GivenNumbered(6);

        var first = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 2));

        var travelled = QueryCursor.Parse(first.NextCursor!.Token);

        var second = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 2).Continuing(travelled));

        Assert.Equal([2L, 3L], second.Records.Select(static record => record["value"]));
    }

    /// <summary>
    /// BR-3's acceptance condition, and the reason a cursor is the ordered index key rather than
    /// the sort value: ten thousand records sharing a sort value are ten thousand records at the
    /// same place, and a boundary among them must neither skip one nor repeat one.
    /// </summary>
    [Fact]
    public void Paging_a_collection_where_every_sort_value_is_the_same_is_exact()
    {
        const int howMany = 10_000;
        const int page = 250;

        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text, required: true),
            new ColumnDefinition("day", ColumnType.Date)
        ]));

        var sameDay = new DateOnly(2026, 3, 1);

        storage.InUnitOfWork(() =>
        {
            foreach (var i in Enumerable.Range(0, howMany))
            {
                storage.Create("readings", new Dictionary<string, object?>
                {
                    ["label"] = $"reading {i:00000}", ["day"] = sameDay
                });
            }
        });

        var seen = new List<string>(howMany);
        QueryCursor? cursor = null;

        while (true)
        {
            var result = storage.ExecuteQuery(new StorageQuery(
                "readings", orderBy: [new QuerySort("day")], take: page).Continuing(cursor));

            seen.AddRange(result.Records.Select(static record => (string)record["label"]!));

            if (!result.HasMore) break;

            cursor = result.NextCursor;
        }

        Assert.Equal(howMany, seen.Count);
        Assert.Equal(howMany, seen.Distinct().Count());
    }

    [Fact]
    public void A_page_marker_made_for_another_ordering_is_refused()
    {
        var storage = GivenNumbered(4);

        var first = storage.ExecuteQuery(new StorageQuery(
            "readings", orderBy: [new QuerySort("value")], take: 2));

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery("readings", orderBy: [new QuerySort("value"), new QuerySort("label")], take: 2)
                .Continuing(first.NextCursor)));

        Assert.Single(thrown.Errors.OfType<CursorDoesNotFit>());
    }

    // ---- Aggregation (D-16) ---------------------------------------------------------------------

    [Fact]
    public void A_query_can_ask_for_totals_over_everything_that_matched()
    {
        var storage = GivenNumbered(10);

        var result = storage.ExecuteQuery(new StorageQuery(
                "readings",
                [new QueryCondition("value", QueryOperator.LessThan, 4L)],
                orderBy: [new QuerySort("value")],
                take: 2)
            .Computing(
                new QueryAggregate(AggregateFunction.Count),
                new QueryAggregate(AggregateFunction.Sum, "cost"),
                new QueryAggregate(AggregateFunction.Minimum, "cost"),
                new QueryAggregate(AggregateFunction.Maximum, "cost"),
                new QueryAggregate(AggregateFunction.Average, "value")));

        // The page is two records; the totals are over the four that matched.
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(4L, result.Aggregates["count"]);
        Assert.Equal(46m, result.Aggregates["sum(cost)"]);
        Assert.Equal(10m, result.Aggregates["minimum(cost)"]);
        Assert.Equal(13m, result.Aggregates["maximum(cost)"]);
        Assert.Equal(1.5m, result.Aggregates["average(value)"]);
    }

    /// <summary>
    /// A total of no values is nothing rather than zero, because zero is a claim - "you spent
    /// nothing" - and a collection with no matching records supports no such claim. A count is
    /// the one that really is zero.
    /// </summary>
    [Fact]
    public void A_total_of_nothing_is_nothing_and_a_count_of_nothing_is_zero()
    {
        var storage = GivenNumbered(3);

        var result = storage.ExecuteQuery(new StorageQuery(
                "readings", [new QueryCondition("value", QueryOperator.GreaterThan, 100L)])
            .Computing(
                new QueryAggregate(AggregateFunction.Count),
                new QueryAggregate(AggregateFunction.Sum, "cost")));

        Assert.Equal(0L, result.Aggregates["count"]);
        Assert.Null(result.Aggregates["sum(cost)"]);
    }

    [Fact]
    public void A_total_of_something_that_cannot_be_added_up_is_refused()
    {
        var storage = GivenNumbered(1);

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery("readings").Computing(new QueryAggregate(AggregateFunction.Sum, "label"))));

        var refused = Assert.Single(thrown.Errors.OfType<AggregateNotSuitable>());
        Assert.Equal("label", refused.ColumnName);
        Assert.Equal(AggregateFunction.Sum, refused.Function);
    }

    // ---- Asking about many values at once (SC-9) -------------------------------------------------

    [Fact]
    public void Many_values_can_be_checked_against_a_column_in_one_call()
    {
        var storage = GivenNumbered(100);

        var asked = new object?[] { "reading 0003", "reading 0040", "reading 0099", "reading 9999", null };

        var result = storage.MatchValues("readings", "label", asked);

        Assert.Equal(3, result.Found.Count);
        Assert.True(result.Contains("reading 0003"));
        Assert.True(result.Contains("reading 0040"));
        Assert.False(result.Contains("reading 9999"));

        var holder = result.Holder("reading 0040");
        Assert.NotNull(holder);
        Assert.Equal("reading 0040", storage.GetById("readings", holder.Value)!["label"]);

        // SC-7a: whatever it did, it says so, and the figures hold together.
        Assert.True(Enum.IsDefined(result.Execution.Access));
        Assert.Equal(3, result.Execution.RecordsReturned);
    }

    /// <summary>
    /// The comparison is the one the index that would answer it uses: text folded, so a value
    /// asked for in another case is the same value (see <c>TextComparison</c>).
    /// </summary>
    [Fact]
    public void Checking_a_set_of_values_compares_text_the_way_a_unique_column_does()
    {
        var storage = GivenNumbered(5);

        Assert.True(storage.MatchValues("readings", "label", ["READING 0002"]).Contains("READING 0002"));
    }

    [Fact]
    public void Checking_a_set_of_values_against_a_column_that_is_not_there_is_refused()
    {
        var storage = GivenNumbered(1);

        Assert.Throws<UnknownColumnException>(() => storage.MatchValues("readings", "nonsense", ["a"]));
    }

    // ---- The narrow face (D-16) -------------------------------------------------------------------

    /// <summary>
    /// What a model emits, expanded by C# into what the storage runs. The expansion is mechanical
    /// - nothing here decides anything the model did not say - and everything it produced is
    /// judged against the schema afterwards, exactly as a caller's own query is.
    /// </summary>
    [Fact]
    public void A_query_a_model_wrote_is_expanded_and_run_like_any_other()
    {
        var storage = GivenNumbered(10);

        var query = ModelQueries.Expand(new ModelQuery(
            "readings",
            [
                new ModelCondition("value", ModelOperator.AtLeast, 5L),
                new ModelCondition("label", ModelOperator.Contains, "reading")
            ],
            orderBy: "value",
            newestFirst: true,
            limit: 3));

        var result = storage.ExecuteQuery(query);

        Assert.Equal([9L, 8L, 7L], result.Records.Select(static record => record["value"]));
    }

    [Fact]
    public void A_query_a_model_wrote_naming_a_column_that_is_not_there_is_refused_with_the_column_named()
    {
        var storage = GivenNumbered(1);

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            ModelQueries.Expand(new ModelQuery(
                "readings", [new ModelCondition("venue", ModelOperator.Is, "Prague")]))));

        Assert.Equal("venue", Assert.Single(thrown.Errors.OfType<UnknownColumn>()).ColumnName);
    }
}
