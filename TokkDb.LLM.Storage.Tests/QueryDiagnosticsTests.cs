using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// UI-4 end to end: a query written as a <see cref="StorageQuery"/>, translated into the
/// engine's form, planned against the indexes that exist, run, and reported to the
/// application's diagnostics service.
///
/// The whole path in one test on purpose. Each half is covered on its own — the translation
/// in StorageQueryTranslationTests, the planning in the engine's QueryPlannerTests — and what
/// is left to check here is that they meet, because the two speak different vocabularies and
/// the only thing that can tell is a query that goes all the way through.
/// </summary>
public sealed class QueryDiagnosticsTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-query-{Ulid.NewUlid()}.db");

    private static CollectionDefinition Article() => new(
        "Article",
        "A publication record",
        [
            new ColumnDefinition("Title", ColumnType.String),
            new ColumnDefinition("Year", ColumnType.Int32),
            new ColumnDefinition("Institution", ColumnType.String)
        ]);

    private const int Records = 120;
    private const int Years = 20;

    private TokkDbStorage NewStorage(IDiagnosticsService diagnostics)
    {
        var storage = new TokkDbStorage(_databaseFilePath, diagnostics);
        storage.CreateCollection(Article());
        for (var i = 0; i < Records; i++)
        {
            storage.Create("Article", new Dictionary<string, object?>
            {
                ["Title"] = $"Article {i}",
                ["Year"] = 2000 + i % Years,
                ["Institution"] = $"Institution {i % 3}"
            });
        }

        return storage;
    }

    /// <summary>
    /// A lookup by identity is the primary-index path, and the report says so with the pages
    /// it cost. This is the shape the diagnostics page shows.
    /// </summary>
    [Fact]
    public void ALookupByIdIsReportedAsThePrimaryIndexPath()
    {
        var diagnostics = new DiagnosticsService();
        using var storage = NewStorage(diagnostics);
        var created = storage.Create("Article", new Dictionary<string, object?>
        {
            ["Title"] = "Проєктування СКБД",
            ["Year"] = 2026,
            ["Institution"] = "КПІ"
        });
        diagnostics.Clear();

        var found = storage.GetById("Article", created.Id);

        Assert.NotNull(found);
        var logged = Assert.Single(diagnostics.Events);
        Assert.Equal("Query", logged.Category);
        Assert.Equal("primary index lookup on Article by id", logged.Title);
        Assert.Equal(DiagnosticLevel.Information, logged.Level);
        Assert.Contains("page reads", logged.Summary);
    }

    /// <summary>
    /// A scan is reported at Warning. It is the one outcome a reader can act on — by adding
    /// an index — and at Information it would read like every query that is already fine.
    /// </summary>
    [Fact]
    public void AFullScanIsReportedLouderThanASeek()
    {
        var diagnostics = new DiagnosticsService();
        using var storage = NewStorage(diagnostics);
        diagnostics.Clear();

        storage.GetAll("Article");

        var logged = Assert.Single(diagnostics.Events);
        Assert.Equal(DiagnosticLevel.Warning, logged.Level);
        Assert.Equal("full scan of Article (no predicate)", logged.Title);
    }

    /// <summary>
    /// The done-when of the phase, from the outside: a query the application wrote, planned
    /// against the index the application created, reading only what it needed.
    /// </summary>
    [Fact]
    public void AQueryOnAnIndexedColumnIsPlannedAsASeekAndReported()
    {
        var diagnostics = new DiagnosticsService();
        using var storage = NewStorage(diagnostics);
        storage.CreateIndex("Article", "Year");
        diagnostics.Clear();

        var result = storage.RunQuery(Bind("Year", "eq", "2005"));

        Assert.Equal(Records / Years, result.Records.Count);
        Assert.Equal("index seek on Article.Year", result.Report.AccessPath);
        // The seek looked at exactly the records it kept: the other 114 were never read at
        // all, let alone turned into documents.
        Assert.Equal(Records / Years, result.Report.RecordsExamined);
        Assert.Equal(Records / Years, result.Report.DocumentsMaterialised);
        var logged = Assert.Single(diagnostics.Events);
        Assert.Equal("index seek on Article.Year", logged.Title);
        Assert.Equal(DiagnosticLevel.Information, logged.Level);
    }

    /// <summary>
    /// The same query with no index on the column: the same answer, a different path, and the
    /// difference visible rather than only slower.
    /// </summary>
    [Fact]
    public void TheSameQueryWithoutAnIndexScansAndSaysWhichColumnIsMissingOne()
    {
        var diagnostics = new DiagnosticsService();
        using var storage = NewStorage(diagnostics);
        diagnostics.Clear();

        var result = storage.RunQuery(Bind("Year", "eq", "2005"));

        Assert.Equal(Records / Years, result.Records.Count);
        Assert.Equal("full scan of Article (no index on Year)", result.Report.AccessPath);
        // The rule the phase is measured by: every record looked at, six documents built.
        Assert.Equal(Records, result.Report.RecordsExamined);
        Assert.Equal(Records / Years, result.Report.DocumentsMaterialised);
        Assert.Equal(DiagnosticLevel.Warning, Assert.Single(diagnostics.Events).Level);
    }

    /// <summary>
    /// A predicate that is partly indexable: the conjunct chooses the path and the OR beside
    /// it is applied to what that path returned.
    /// </summary>
    [Fact]
    public void AnIndexedConjunctChoosesThePathAndTheRestIsAppliedPerRecord()
    {
        var diagnostics = new DiagnosticsService();
        using var storage = NewStorage(diagnostics);
        storage.CreateIndex("Article", "Year");
        diagnostics.Clear();

        var query = new RecordQuery
        {
            CollectionName = "Article",
            Where = new RecordFilter
            {
                Logic = "and",
                Filters =
                [
                    new RecordFilter { Field = "Year", Operator = "eq", Value = "2005" },
                    new RecordFilter
                    {
                        Logic = "or",
                        Filters =
                        [
                            new RecordFilter { Field = "Institution", Operator = "eq", Value = "Institution 1" },
                            new RecordFilter { Field = "Institution", Operator = "eq", Value = "Institution 7" }
                        ]
                    }
                ]
            }
        };

        var result = storage.RunQuery(Bound(query));

        Assert.Equal("index seek on Article.Year", result.Report.AccessPath);
        Assert.True(result.Report.HasResidual);
        // The seek returned the six records of 2005; the OR then removed the ones whose
        // institution is neither of the two named.
        Assert.Equal(Records / Years, result.Report.RecordsExamined);
        Assert.NotEmpty(result.Records);
        Assert.True(result.Records.Count < Records / Years,
            "the residual should have removed some of the seek's records");
        Assert.All(result.Records, record =>
        {
            Assert.Equal(2005, Convert.ToInt32(record.Value["Year"]));
            Assert.Equal("Institution 1", record.Value["Institution"]?.ToString());
        });
    }

    private static StorageQuery Bind(string column, string op, string value)
    {
        return Bound(new RecordQuery
        {
            CollectionName = "Article",
            Where = new RecordFilter { Field = column, Operator = op, Value = value }
        });
    }

    /// <summary>
    /// Bound against the schema rather than against the database. The binder reads a schema
    /// and produces a query in terms of column definitions; nothing in what it produces knows
    /// which storage will run it, which is the property that makes one query representation
    /// worth having. Binding against a MemoryStorage carrying the same definitions keeps this
    /// test off the parts of the Phase 4 skeleton that are still unimplemented.
    /// </summary>
    private static StorageQuery Bound(RecordQuery query)
    {
        var schema = new MemoryStorage();
        schema.CreateCollection(Article());
        return new RecordQueryBinder().Bind(schema, query);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databaseFilePath, _databaseFilePath + ".wal", _databaseFilePath + ".lock" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
