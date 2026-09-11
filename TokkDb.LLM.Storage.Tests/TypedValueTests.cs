using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// What Long, Decimal, DateTime and Guid gaining their own document values bought.
///
/// They used to be written as invariant text, because <c>ValueTypeEnum</c> declared them and
/// nothing implemented them. §2.2 recorded two consequences: a value of the wrong type made a
/// record unreadable forever, and an ordered comparison over one of the four could not become
/// an index range, because "250" sorts below "40" as text. Both are checked here.
/// </summary>
public sealed class TypedValueTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-typed-{Ulid.NewUlid()}.db");

    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CollectionDefinition Reading() => new(
        "Reading",
        columns:
        [
            new ColumnDefinition("Label", ColumnType.String),
            new ColumnDefinition("Count", ColumnType.Int64),
            new ColumnDefinition("Price", ColumnType.Decimal),
            new ColumnDefinition("Taken", ColumnType.DateTime),
            new ColumnDefinition("Device", ColumnType.Guid)
        ]);

    private TokkDbStorage NewStorage()
    {
        var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Reading());
        return storage;
    }

    private static Ulid Insert(TokkDbStorage storage, string label, long count, decimal price,
        DateTime taken, Guid device) =>
        storage.Create("Reading", new Dictionary<string, object?>
        {
            ["Label"] = label, ["Count"] = count, ["Price"] = price, ["Taken"] = taken, ["Device"] = device
        }).Id;

    [Fact]
    public void AllFourRoundTripAsThemselvesAcrossARestart()
    {
        var device = Guid.NewGuid();
        var taken = new DateTime(2026, 9, 7, 14, 30, 15, DateTimeKind.Utc);
        Ulid id;

        using (var storage = NewStorage())
        {
            id = Insert(storage, "first", 9_000_000_000L, 1234.50m, taken, device);
        }

        using var reopened = new TokkDbStorage(_databaseFilePath);
        var fields = reopened.GetById("Reading", id)!.Fields;

        Assert.Equal(9_000_000_000L, fields["Count"]);
        Assert.Equal(1234.50m, fields["Price"]);
        Assert.Equal(taken, fields["Taken"]);
        Assert.Equal(device, fields["Device"]);
        //Types, not just values: a caller that asked for an Int64 column gets a long.
        Assert.IsType<long>(fields["Count"]);
        Assert.IsType<decimal>(fields["Price"]);
        Assert.IsType<DateTime>(fields["Taken"]);
        Assert.IsType<Guid>(fields["Device"]);
        //The scale of the decimal and the kind of the moment survive as well as the number.
        Assert.Equal("1234.50", ((decimal)fields["Price"]!).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)fields["Taken"]!).Kind);
    }

    /// <summary>
    /// The one that could not work before: a range over a numeric column, answered by an index.
    /// 40 and 250 are the pair that made the old encoding wrong — as text, "250" sorts below
    /// "40", so a range from 100 upwards would have returned 250 and missed nothing else only
    /// by accident.
    /// </summary>
    [Theory]
    [InlineData("Count")]
    [InlineData("Price")]
    public void ARangeOverANumericColumnIsAnsweredByAnIndexAndInNumericOrder(string column)
    {
        using var storage = NewStorage();
        storage.CreateIndex("Reading", column);
        foreach (var value in new[] { 40, 100, 250, 1000 })
        {
            Insert(storage, $"n{value}", value, value, Epoch.AddDays(value), Guid.NewGuid());
        }

        var definition = storage.GetCollectionDefinition("Reading")!;
        var target = definition.Columns.First(candidate => candidate.Name == column);
        var reports = new List<TokkDb.Pages.Query.QueryReport>();
        storage.Queries.QueryExecuted += reports.Add;

        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(target, QueryOperator.GreaterOrEqual, ["100"]),
            [new StorageSort(target, false)],
            0, 100, []));

        //An index range, not a scan that happened to give the right answer.
        Assert.Equal($"index range on Reading.{column} [GreaterOrEqual 100]",
            Assert.Single(reports).AccessPath);
        //100, 250 and 1000 — in numeric order, which is exactly what text ordering got wrong.
        Assert.Equal(["n100", "n250", "n1000"], result.Rows.Select(row => row.Fields["Label"]));
    }

    [Fact]
    public void ARangeOverADateTimeColumnIsAnsweredByAnIndex()
    {
        using var storage = NewStorage();
        storage.CreateIndex("Reading", "Taken");
        foreach (var day in new[] { 1, 40, 100, 250 })
        {
            Insert(storage, $"d{day}", day, day, Epoch.AddDays(day), Guid.NewGuid());
        }

        var definition = storage.GetCollectionDefinition("Reading")!;
        var taken = definition.Columns.First(column => column.Name == "Taken");
        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(taken, QueryOperator.LessThan,
                [Epoch.AddDays(100).ToString("O", System.Globalization.CultureInfo.InvariantCulture)]),
            [new StorageSort(taken, false)],
            0, 100, []));

        Assert.Equal(["d1", "d40"], result.Rows.Select(row => row.Fields["Label"]));
    }

    [Fact]
    public void AGuidColumnIsFoundThroughItsIndex()
    {
        using var storage = NewStorage();
        storage.CreateIndex("Reading", "Device");
        var wanted = Guid.NewGuid();
        Insert(storage, "wanted", 1, 1, Epoch, wanted);
        for (var index = 0; index < 20; index++)
        {
            Insert(storage, $"other{index}", index, index, Epoch, Guid.NewGuid());
        }

        var definition = storage.GetCollectionDefinition("Reading")!;
        var device = definition.Columns.First(column => column.Name == "Device");
        var reports = new List<TokkDb.Pages.Query.QueryReport>();
        storage.Queries.QueryExecuted += reports.Add;

        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(device, QueryOperator.Equals, [wanted.ToString("D")]),
            [], 0, 100, []));

        Assert.Equal("index seek on Reading.Device", Assert.Single(reports).AccessPath);
        Assert.Equal("wanted", Assert.Single(result.Rows).Fields["Label"]);
        //One record read out of twenty-one: the index answered it rather than a scan filtering.
        Assert.Equal(1, reports[0].RecordsExamined);
    }

    /// <summary>
    /// The other half of §2.2's blocking row. A wrong-typed value used to make the record
    /// unreadable, and because a scan decodes every record it took the collection with it.
    /// The stored form now says what a value is, so a wrong one is just a wrong one.
    /// </summary>
    [Fact]
    public void AWrongTypedValueNoLongerMakesTheCollectionUnreadable()
    {
        using var storage = NewStorage();
        Insert(storage, "good", 1, 1m, Epoch, Guid.NewGuid());
        storage.Create("Reading", new Dictionary<string, object?>
        {
            ["Label"] = "bad", ["Count"] = "not a number", ["Price"] = 2m,
            ["Taken"] = Epoch, ["Device"] = Guid.NewGuid()
        });

        var all = storage.GetAll("Reading");

        Assert.Equal(2, all.Count);
        Assert.Equal(1L, all.Single(record => Equals(record.Fields["Label"], "good")).Fields["Count"]);
        //Stored as what it was given, and read back as that.
        Assert.Equal("not a number", all.Single(record => Equals(record.Fields["Label"], "bad")).Fields["Count"]);
    }

    /// <summary>
    /// A column widened from Int32 to Int64 keeps the index it already has: the two share a
    /// key tag and a width (D-3), so the entries an Int32 wrote are the entries an Int64 reads.
    /// </summary>
    [Fact]
    public void AColumnWidenedFromInt32ToInt64KeepsItsIndexEntries()
    {
        using var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(new CollectionDefinition("Sample",
            columns: [new ColumnDefinition("Label", ColumnType.String),
                new ColumnDefinition("Value", ColumnType.Int32)]));
        storage.CreateIndex("Sample", "Value");
        foreach (var value in new[] { 40, 100, 250 })
        {
            storage.Create("Sample", new Dictionary<string, object?>
            {
                ["Label"] = $"n{value}", ["Value"] = value
            });
        }

        storage.UpdateColumn("Sample", "Value", new ColumnDefinition("Value", ColumnType.Int64));

        var definition = storage.GetCollectionDefinition("Sample")!;
        var widened = definition.Columns.First(column => column.Name == "Value");
        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(widened, QueryOperator.GreaterOrEqual, ["100"]),
            [new StorageSort(widened, false)],
            0, 100, []));

        Assert.Equal(["n100", "n250"], result.Rows.Select(row => row.Fields["Label"]));
    }

    public void Dispose()
    {
        foreach (var path in new[]
                 {
                     _databaseFilePath,
                     TokkDb.Disk.Journal.GetJournalPath(_databaseFilePath),
                     TokkDb.Disk.WriteLock.GetLockPath(_databaseFilePath)
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
