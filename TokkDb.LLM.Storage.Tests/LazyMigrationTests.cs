using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// DC-7's lazy migration, end to end. A schema change is recorded rather than applied, records
/// written on either side of it read the same way, and an explicit rewrite converges them.
///
/// The point of the decision is what does <em>not</em> happen when the schema changes, so
/// several of these assert an absence: no record rewritten, no page added, the stored headers
/// still carrying the version they were written under.
/// </summary>
public sealed class LazyMigrationTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-migration-{Ulid.NewUlid()}.db");

    private static CollectionDefinition Article() => new(
        "Article",
        "A publication record",
        [
            new ColumnDefinition("Title", ColumnType.String),
            new ColumnDefinition("Year", ColumnType.Int32),
            new ColumnDefinition("Note", ColumnType.String)
        ]);

    private TokkDbStorage NewStorage()
    {
        var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Article());
        return storage;
    }

    // The year column is named explicitly because these tests rename it: a record written
    // after a rename has to be written under the name the column has then, exactly as a
    // caller reading the current definition would.
    private static Ulid Insert(
        TokkDbStorage storage, string title, object? year, string yearColumn = "Year", string note = "n") =>
        storage.Create("Article", new Dictionary<string, object?>
        {
            ["Title"] = title, [yearColumn] = year, ["Note"] = note
        }).Id;

    // =====================================================================
    // The done-when.
    // =====================================================================

    /// <summary>
    /// A retyped column serves records written on both sides of the change, and a rewrite
    /// converges them. Int32 to Int64 is the interesting pair: the engine stores an Int32 as a
    /// document value and an Int64 as invariant text, so the two records genuinely differ on
    /// the page and only the migration makes them read alike.
    /// </summary>
    [Fact]
    public void ARetypedColumnServesOldAndNewRecordsAndARewriteConvergesThem()
    {
        using var storage = NewStorage();
        var beforeChange = Insert(storage, "Old", 1999);

        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Year", ColumnType.Int64));
        var afterChange = Insert(storage, "New", 2026L);

        // Both read as Int64, whichever schema they were written under.
        Assert.Equal(1999L, storage.GetById("Article", beforeChange)!.Fields["Year"]);
        Assert.Equal(2026L, storage.GetById("Article", afterChange)!.Fields["Year"]);
        Assert.Equal([1999L, 2026L],
            storage.GetAll("Article").Select(record => record.Fields["Year"]).OrderBy(year => year));

        // And the rewrite changes nothing about what they say.
        Assert.Equal(1, storage.Rewrite("Article"));
        Assert.Equal(1999L, storage.GetById("Article", beforeChange)!.Fields["Year"]);
        Assert.Equal(2026L, storage.GetById("Article", afterChange)!.Fields["Year"]);
        // Converged: there is nothing left to replay, so a second rewrite has no work.
        Assert.Equal(0, storage.Rewrite("Article"));
    }

    /// <summary>
    /// The other half of the decision: the change itself touches no record. If it did, the
    /// stall and the dead space the decision exists to avoid would be back.
    /// </summary>
    [Fact]
    public void ChangingAColumnRewritesNoRecord()
    {
        using var storage = NewStorage();
        for (var i = 0; i < 500; i++)
        {
            Insert(storage, $"Article {i}", 2000 + i % 20);
        }

        var pagesBefore = PageCount();
        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Published", ColumnType.Int64));
        storage.RemoveColumn("Article", "Note");

        // Two schema changes over 500 records, and the data pages are untouched. What grew is
        // the catalogue document, which gained two migration steps.
        Assert.Equal(pagesBefore, PageCount());
        Assert.All(storage.GetAll("Article"), record => Assert.False(record.Fields.ContainsKey("Note")));
        Assert.Equal(500, storage.GetAll("Article").Count);
        Assert.Equal(2000L, storage.GetAll("Article")
            .OrderBy(record => record.Fields["Title"]?.ToString())
            .First().Fields["Published"]);
    }

    // =====================================================================
    // Reads of every kind have to agree.
    // =====================================================================

    /// <summary>
    /// A query names the column as it is called now, and has to find records written when it
    /// was called something else — through the index as well as through a scan, since the two
    /// take different paths to the record.
    /// </summary>
    [Fact]
    public void AQueryFindsRecordsWrittenBeforeTheColumnWasRenamed()
    {
        using var storage = NewStorage();
        storage.CreateIndex("Article", "Year");
        var before = Insert(storage, "Old", 1999);

        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Published", ColumnType.Int32));
        var after = Insert(storage, "New", 1999, "Published");

        var definition = storage.GetCollectionDefinition("Article")!;
        var published = definition.Columns.First(column => column.Name == "Published");
        var reports = new List<TokkDb.Pages.Query.QueryReport>();
        storage.Queries.QueryExecuted += reports.Add;
        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(published, QueryOperator.Equals, ["1999"]),
            [], 0, 100, []));

        // The index moved to the new name as part of the change, so this is a seek and not a
        // scan that happens to give the right answer.
        Assert.Equal("index seek on Article.Published", Assert.Single(reports).AccessPath);
        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, row => row.Id == before);
        Assert.Contains(result.Rows, row => row.Id == after);
    }

    /// <summary>
    /// The index is rebuilt eagerly because there is no lazy version of it: a query encodes
    /// its constant as the column's current type, so an index still holding keys made from the
    /// old one would answer with the wrong records rather than slowly.
    /// </summary>
    [Fact]
    public void AnIndexIsRebuiltFromTheMigratedValuesWhenAColumnIsRetyped()
    {
        using var storage = NewStorage();
        storage.CreateIndex("Article", "Year");
        var before = Insert(storage, "Old", 40);
        Insert(storage, "Also old", 250);

        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Year", ColumnType.Int64));

        var definition = storage.GetCollectionDefinition("Article")!;
        var year = definition.Columns.First(column => column.Name == "Year");
        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(year, QueryOperator.Equals, ["40"]),
            [], 0, 100, []));

        Assert.Equal(before, Assert.Single(result.Rows).Id);
    }

    /// <summary>
    /// A record whose stored value cannot be read as the new type has no value for that column
    /// — the same situation as a record written before the column existed. It must not throw,
    /// and it must not take the rest of the collection with it.
    /// </summary>
    [Fact]
    public void AValueThatCannotSurviveARetypeReadsAsNothing()
    {
        using var storage = NewStorage();
        var readable = Insert(storage, "Numeric", 2020, note: "2020");
        var unreadable = Insert(storage, "Not numeric", 2020, note: "sometime in the nineties");

        storage.UpdateColumn("Article", "Note", new ColumnDefinition("Note", ColumnType.Int32));

        Assert.Equal(2020, storage.GetById("Article", readable)!.Fields["Note"]);
        Assert.Null(storage.GetById("Article", unreadable)!.Fields["Note"]);
        Assert.Equal(2, storage.GetAll("Article").Count);
    }

    [Fact]
    public void ARemovedColumnStopsBeingReadWithoutTheRecordsBeingTouched()
    {
        using var storage = NewStorage();
        var id = Insert(storage, "Article", 2020, "a note");

        storage.RemoveColumn("Article", "Note");

        Assert.False(storage.GetById("Article", id)!.Fields.ContainsKey("Note"));
        // Added again under the same name: a different column, and the old value is not its.
        storage.AddColumn("Article", new ColumnDefinition("Note", ColumnType.String));
        Assert.False(storage.GetById("Article", id)!.Fields.ContainsKey("Note"));
    }

    /// <summary>
    /// Changes stack. A column renamed, retyped and renamed again still resolves back to what
    /// the oldest record calls it.
    /// </summary>
    [Fact]
    public void SeveralChangesToOneColumnStackOverARecordThatPredatesThemAll()
    {
        using var storage = NewStorage();
        var oldest = Insert(storage, "Oldest", 1999);

        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Published", ColumnType.Int32));
        var middle = Insert(storage, "Middle", 2005, "Published");
        storage.UpdateColumn("Article", "Published", new ColumnDefinition("Published", ColumnType.Int64));
        storage.UpdateColumn("Article", "Published", new ColumnDefinition("PublishedYear", ColumnType.Int64));
        var newest = Insert(storage, "Newest", 2026L, "PublishedYear");

        Assert.Equal(1999L, storage.GetById("Article", oldest)!.Fields["PublishedYear"]);
        Assert.Equal(2005L, storage.GetById("Article", middle)!.Fields["PublishedYear"]);
        Assert.Equal(2026L, storage.GetById("Article", newest)!.Fields["PublishedYear"]);

        Assert.Equal(2, storage.Rewrite("Article"));
        Assert.Equal([1999L, 2005L, 2026L], storage.GetAll("Article")
            .Select(record => record.Fields["PublishedYear"]).OrderBy(year => year));
    }

    // =====================================================================
    // Rewrite.
    // =====================================================================

    /// <summary>
    /// After a rewrite the log is empty, so a read replays nothing. That is what "converges"
    /// means: the records and the schema agree again, and the cost of the change is finally
    /// paid — at a moment the caller chose.
    /// </summary>
    [Fact]
    public void ARewriteEmptiesTheMigrationLogAndSurvivesAReopen()
    {
        Ulid oldest;
        using (var storage = NewStorage())
        {
            oldest = Insert(storage, "Old", 1999);
            storage.UpdateColumn("Article", "Year", new ColumnDefinition("Published", ColumnType.Int64));
            storage.RemoveColumn("Article", "Note");
            Assert.Equal(1, storage.Rewrite("Article"));
        }

        using var reopened = new TokkDbStorage(_databaseFilePath);
        Assert.Equal(1999L, reopened.GetById("Article", oldest)!.Fields["Published"]);
        Assert.False(reopened.GetById("Article", oldest)!.Fields.ContainsKey("Note"));
        // Nothing left to converge.
        Assert.Equal(0, reopened.Rewrite("Article"));
    }

    /// <summary>
    /// The rewrite runs in batches rather than one transaction, so partial progress is a state
    /// the database has to be able to be in. It is the state lazy migration already handles —
    /// a converged record is at the current version and an unconverged one is read through the
    /// log — which is why the log is only dropped at the end.
    /// </summary>
    [Fact]
    public void ARewriteOfManyRecordsConvergesThemAllAndIsIdempotent()
    {
        using var storage = NewStorage();
        for (var i = 0; i < 1_200; i++)
        {
            Insert(storage, $"Article {i}", 2000 + i % 20);
        }

        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Published", ColumnType.Int64));

        Assert.Equal(1_200, storage.Rewrite("Article"));
        Assert.Equal(0, storage.Rewrite("Article"));
        Assert.Equal(1_200, storage.GetAll("Article").Count);
        Assert.All(storage.GetAll("Article"), record => Assert.IsType<long>(record.Fields["Published"]));
    }

    /// <summary>
    /// A rewrite may move a record to a slot that fits it, and the index entries have to move
    /// with it (D-2) or a lookup would land on whatever now occupies the old slot.
    /// </summary>
    [Fact]
    public void IndexesStillFindRecordsARewriteMoved()
    {
        using var storage = NewStorage();
        storage.CreateIndex("Article", "Year");
        var ids = new List<Ulid>();
        for (var i = 0; i < 200; i++)
        {
            ids.Add(Insert(storage, $"Article {i}", 2000 + i % 20));
        }

        // Retyping to Int64 stores the value as text, which is longer than the Int32 it
        // replaces, so the rewritten records do not all fit where they were.
        storage.UpdateColumn("Article", "Year", new ColumnDefinition("Year", ColumnType.Int64));
        storage.Rewrite("Article");

        var definition = storage.GetCollectionDefinition("Article")!;
        var year = definition.Columns.First(column => column.Name == "Year");
        var result = storage.ExecuteQuery(new StorageQuery(
            definition, new StorageFieldFilter(year, QueryOperator.Equals, ["2005"]), [], 0, 100, []));

        Assert.Equal(10, result.Rows.Count);
        Assert.All(ids, id => Assert.NotNull(storage.GetById("Article", id)));
    }

    [Fact]
    public void ARewriteOfACollectionThatNeverChangedDoesNothing()
    {
        using var storage = NewStorage();
        Insert(storage, "Article", 2020);

        Assert.Equal(0, storage.Rewrite("Article"));
        Assert.Throws<InvalidOperationException>(() => storage.Rewrite("NoSuchCollection"));
    }

    private long PageCount() => new FileInfo(_databaseFilePath).Length / 8192;

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
