using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Browsing what is stored (BR-1 to BR-7, BR-10, D-13, Phase 6): the overview from maintained
/// figures, the table a page at a time with exact boundaries among ties, the changed-underneath
/// indicator, sort and filter as the declarative query, the structure in plain words, the record
/// with its empty fields and its relations, and CSV that keeps the sort and the filter.
/// </summary>
public sealed class BrowsingTests
{
    private static MemoryStorage Given(int conferences = 40)
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("conferences", "conferences I went to", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, required: true, unique: true),
            new ColumnDefinition("city", ColumnType.Text),
            new ColumnDefinition("date", ColumnType.Date),
            new ColumnDefinition("cost", ColumnType.Decimal),
            new ColumnDefinition("notes", ColumnType.Text)
        ]));
        storage.CreateCollection(new CollectionDefinition("expenses", "money I spent", columns:
        [
            new ColumnDefinition("what", ColumnType.Text, required: true),
            new ColumnDefinition("conference", ColumnType.Text),
            new ColumnDefinition("amount", ColumnType.Decimal)
        ]));
        storage.AddRelation(new RelationDefinition("expense_conference", "expenses", "conference", "conferences", "name", purpose: "the conference this was spent at"));

        for (var i = 0; i < conferences; i++)
        {
            storage.Create("conferences", new Dictionary<string, object?>
            {
                ["name"] = $"Conf {i:00}",
                ["city"] = i % 3 == 0 ? "Lviv" : "Kyiv",
                ["date"] = new DateOnly(2025, 1 + i % 12, 1),
                ["cost"] = (decimal)(100 * (i % 7)),
                ["notes"] = i % 4 == 0 ? null : $"note {i}"
            });
        }

        storage.Create("expenses", new Dictionary<string, object?> { ["what"] = "tickets", ["conference"] = "Conf 03", ["amount"] = 100m });
        storage.Create("expenses", new Dictionary<string, object?> { ["what"] = "hotel", ["conference"] = "Conf 03", ["amount"] = 300m });
        return storage;
    }

    [Fact]
    public void The_overview_lists_everything_most_recently_changed_first_in_plain_words()
    {
        var storage = Given();
        var cards = ThingsOverview.Of(storage);

        Assert.Equal(["expenses", "conferences"], cards.Select(static card => card.Thing));
        Assert.Equal("Conferences", cards[1].Title);
        Assert.Equal("conferences I went to", cards[1].Keeps);
        Assert.Equal("40 of them", cards[1].Count);
        Assert.Equal("just now", cards[1].When);
        foreach (var word in WhatItKeeps.ForbiddenWords) Assert.DoesNotContain(word, (cards[1].Title + cards[1].Keeps + cards[1].Count).ToLowerInvariant());
    }

    /// <summary>BR-3: a page boundary among many rows sharing a sort value neither skips nor repeats one.</summary>
    [Fact]
    public void Paging_among_equal_sort_values_is_exact()
    {
        var storage = Given(60);
        var table = new RecordTable(storage, "conferences", pageSize: 7);

        Assert.Null(table.Open(new QuerySort("cost")));
        Assert.Equal(60, table.Total);
        while (table.HasMore) Assert.Null(table.More());

        Assert.Equal(60, table.Rows.Count);
        Assert.Equal(60, table.Rows.Select(static row => row.Id).Distinct().Count());
        Assert.Equal(table.Rows.Select(static row => (decimal)row.Record["cost"]!), table.Rows.Select(static row => (decimal)row.Record["cost"]!).OrderBy(static cost => cost));

        // The display value leads the columns (BR-2).
        Assert.Equal("name", table.Columns[0].Name);
        Assert.StartsWith("Conf", table.Rows[0].Title);
    }

    /// <summary>BR-3a: changing the sort starts a new sequence at the top, and a cursor of one sort is refused by another.</summary>
    [Fact]
    public void Changing_the_sort_starts_again_at_the_top()
    {
        var storage = Given();
        var table = new RecordTable(storage, "conferences", pageSize: 10);
        table.Open(new QuerySort("cost", Descending: true));
        table.More();
        Assert.Equal(20, table.Rows.Count);

        table.Open(new QuerySort("date"));

        Assert.Equal(10, table.Rows.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), table.Rows[0].Record["date"]);
    }

    /// <summary>BR-3b: a record edited so that it moves in the order produces the changed indicator, driven by the maintained time.</summary>
    [Fact]
    public void Editing_a_record_mid_sequence_shows_that_the_thing_has_changed()
    {
        var storage = Given();
        var table = new RecordTable(storage, "conferences", pageSize: 10);
        table.Open(new QuerySort("cost"));
        Assert.False(table.HasChangedUnderneath);

        Thread.Sleep(5);
        var moved = table.Rows[0].Record;
        storage.Update(moved.With("cost", 9_999m));

        Assert.True(table.HasChangedUnderneath);
        Assert.Null(table.Restart());
        Assert.False(table.HasChangedUnderneath);
        Assert.Equal(40, table.Total);
    }

    /// <summary>BR-4: sorting and filtering are the declarative query, cost no tokens, and a filter is visible and removable.</summary>
    [Fact]
    public void Filters_narrow_the_table_and_read_as_words()
    {
        var storage = Given();
        var table = new RecordTable(storage, "conferences", pageSize: 100);
        var filters = new[] { new TableFilter("city", FilterKind.Is, "Lviv"), new TableFilter("cost", FilterKind.Over, "100") };

        Assert.Null(table.Open(new QuerySort("cost", Descending: true), filters));

        Assert.All(table.Rows, row => Assert.Equal("Lviv", row.Record["city"]));
        Assert.All(table.Rows, row => Assert.True((decimal)row.Record["cost"]! > 100));
        Assert.Equal(table.Rows.Count, table.Total);
        Assert.Equal("city is Lviv", filters[0].Describe());
        Assert.Equal("cost over 100", filters[1].Describe());

        // A value that does not read as the field's kind is a stated problem, not a silent empty table.
        var wrong = new RecordTable(storage, "conferences");
        Assert.Contains("is not an amount", wrong.Open(null, [new TableFilter("cost", FilterKind.Over, "lots")]));

        Assert.Contains(FilterKind.Contains, TableFilter.KindsFor(ColumnType.Text));
        Assert.DoesNotContain(FilterKind.Over, TableFilter.KindsFor(ColumnType.Text));
    }

    /// <summary>BR-10: what is on screen, with its sort and filter, as CSV - every page of it.</summary>
    [Fact]
    public void Csv_keeps_the_sort_and_the_filter()
    {
        var storage = Given();
        var table = new RecordTable(storage, "conferences", pageSize: 5);
        table.Open(new QuerySort("cost", Descending: true), [new TableFilter("city", FilterKind.Is, "Kyiv")]);

        var csv = table.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("name,city,date,cost,notes", csv[0]);
        Assert.Equal(table.Total + 1, csv.Length);
        Assert.All(csv.Skip(1), line => Assert.Contains(",Kyiv,", line));
        var costs = csv.Skip(1).Select(line => decimal.Parse(line.Split(',')[3], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(costs.OrderByDescending(static cost => cost), costs);
    }

    /// <summary>BR-6: the structure panel in plain words, with none of the forbidden vocabulary.</summary>
    [Fact]
    public void What_it_keeps_is_in_plain_words()
    {
        var storage = Given();
        var fields = WhatItKeeps.Describe(storage.GetCollectionDefinition("conferences")!, storage.GetAll("conferences"));

        Assert.Equal(["name", "city", "date", "cost", "notes"], fields.Select(static field => field.Name));
        Assert.Equal("some words", fields[0].Kind);
        Assert.Contains("always needed", fields[0].Notes);
        Assert.Contains("no two the same", fields[0].Notes);
        Assert.Equal("a day", fields[2].Kind);
        Assert.Equal("an amount", fields[3].Kind);
        Assert.Contains(fields[4].Notes, static note => note.StartsWith("empty in 10 of 40", StringComparison.Ordinal));

        var text = string.Join(" ", fields.Select(static field => field.Sentence)).ToLowerInvariant();
        foreach (var word in WhatItKeeps.ForbiddenWords) Assert.DoesNotContain(word, text);
    }

    /// <summary>BR-5, BR-7: every field, empty ones empty; related records one step away under a heading a person would write.</summary>
    [Fact]
    public void A_record_shows_every_field_and_what_it_relates_to()
    {
        var storage = Given();
        storage.AddColumn("conferences", new ColumnDefinition("venue", ColumnType.Text));
        var conf = storage.GetAll("conferences").Single(static record => record["name"] is "Conf 03");

        var detail = RecordDetail.Open(storage, "conferences", conf.Id)!;

        Assert.Equal("Conf 03", detail.Title);
        Assert.Equal(["name", "city", "date", "cost", "notes", "venue"], detail.Fields.Select(static field => field.Name));
        var venue = detail.Fields.Single(static field => field.Name == "venue");
        Assert.True(venue.IsEmpty);
        Assert.Equal("", venue.Value);

        var related = Assert.Single(detail.Related);
        Assert.Equal("The conference this was spent at", WhatItKeeps.Heading(storage.GetRelations().Single(), fromThisThing: true));
        Assert.Contains("that point here as the conference this was spent at", related.Heading);
        Assert.Equal(2, related.Records.Count);
        Assert.Equal("expenses", related.Thing);

        var expense = RecordDetail.Open(storage, "expenses", related.Records[0].Id)!;
        var back = Assert.Single(expense.Related);
        Assert.Equal("The conference this was spent at", back.Heading);
        Assert.Equal("Conf 03", Assert.Single(back.Records).Title);
    }
}

/// <summary>BR-2, BR-3 on the engine: ten thousand records open in under two seconds, the planner reports an index walk, and a page costs a page.</summary>
public sealed class BrowsingEngineTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("browsing");

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Ten_thousand_records_page_through_an_index_walk_a_page_at_a_time()
    {
        using var storage = new TokkDbStorage(_database.FilePath);
        storage.CreateCollection(new CollectionDefinition("events", "events", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, required: true),
            new ColumnDefinition("cost", ColumnType.Decimal),
            new ColumnDefinition("held_on", ColumnType.Date)
        ]));

        var random = new Random(7);
        storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                storage.Create("events", new Dictionary<string, object?>
                {
                    ["title"] = $"Event {i}",
                    ["cost"] = (decimal)random.Next(0, 500),
                    ["held_on"] = new DateOnly(2025, 1, 1).AddDays(random.Next(0, 365))
                });
            }
        });

        var table = new RecordTable(storage, "events", pageSize: 20);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(table.Open(new QuerySort("cost")));

        var opened = clock.Elapsed;

        Assert.True(opened < TimeSpan.FromSeconds(2), $"opening took {opened.TotalSeconds:F2}s");
        Assert.Equal(20, table.Rows.Count);
        Assert.Equal(10_000, table.Total);
        Assert.True(table.LastExecution!.Access is QueryAccess.RangeWalk, table.LastExecution.ToString());

        // The next page, and the next: each costs about a page's worth, not the collection's.
        var readsBefore = storage.PageReadCount;
        Assert.Null(table.More());
        var secondPageReads = storage.PageReadCount - readsBefore;

        Assert.Equal(40, table.Rows.Count);
        Assert.True(table.LastExecution!.Access is QueryAccess.RangeWalk, table.LastExecution.ToString());
        Assert.True(secondPageReads < 200, $"the second page read {secondPageReads} pages");
        Assert.Equal(table.Rows.Select(static row => (decimal)row.Record["cost"]!), table.Rows.Select(static row => (decimal)row.Record["cost"]!).Order());
        Assert.Equal(40, table.Rows.Select(static row => row.Id).Distinct().Count());

        // Paging among equal values is exact on the walk too: 10 000 records over 500 values is
        // twenty of each, and a page boundary lands inside a run every time.
        while (table.More() is null && table.HasMore) { }
        Assert.Equal(10_000, table.Rows.Count);
        Assert.Equal(10_000, table.Rows.Select(static row => row.Id).Distinct().Count());
    }

    /// <summary>
    /// A limitation, recorded rather than hidden (R-1): the engine walks an index forward only -
    /// there is no backward leaf chain - so a descending sort is a sort, not a walk. It examines
    /// every record for each page, keeping only a page's worth in memory, and is still exact and
    /// still quick at ten thousand; the cost grows with the thing, not with the page.
    /// </summary>
    [Fact]
    public void A_descending_sort_is_exact_and_quick_but_is_a_sort_rather_than_a_walk()
    {
        using var storage = new TokkDbStorage(_database.FilePath);
        storage.CreateCollection(new CollectionDefinition("events", "events", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, required: true),
            new ColumnDefinition("cost", ColumnType.Decimal)
        ]));

        var random = new Random(11);
        storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                storage.Create("events", new Dictionary<string, object?> { ["title"] = $"Event {i}", ["cost"] = (decimal)random.Next(0, 500) });
            }
        });

        var table = new RecordTable(storage, "events", pageSize: 20);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(table.Open(new QuerySort("cost", Descending: true)));
        Assert.Null(table.More());

        // Generous, because this runs beside the rest of the suite: the requirement's two seconds is the walk's, measured on its own in the performance report.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"two pages took {clock.Elapsed.TotalSeconds:F2}s");
        Assert.Equal(40, table.Rows.Count);
        Assert.Equal(10_000, table.Total);
        // The second page is bounded by the cursor, so its access is the range below it - walked
        // forward and then sorted, which the description says.
        Assert.Contains("walked forward only", table.LastExecution!.Description);
        Assert.Equal(table.Rows.Select(static row => (decimal)row.Record["cost"]!), table.Rows.Select(static row => (decimal)row.Record["cost"]!).OrderByDescending(static cost => cost));
        Assert.Equal(40, table.Rows.Select(static row => row.Id).Distinct().Count());
    }
}
