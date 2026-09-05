using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;
using TokkDb.Pages.Query;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// What only a real file can show: that the adapter works against one, and that what it wrote
/// is still there after the file is closed and opened again. The behaviour of each member is
/// the shared contract suite's business — this is about durability, which that suite cannot
/// see because it never reopens anything.
/// </summary>
public sealed class TokkDbStorageSmokeTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-storage-{Ulid.NewUlid()}.db");

    private static CollectionDefinition Article() => new(
        "Article",
        "A publication record",
        [
            new ColumnDefinition("Title", ColumnType.String, "The title as published"),
            new ColumnDefinition("Year", ColumnType.Int32),
            new ColumnDefinition("Reviewed", ColumnType.Boolean)
        ]);

    [Fact]
    public void ARecordIsCreatedReadUpdatedAndDeletedThroughIStorage()
    {
        var id = Ulid.Empty;

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            storage.CreateCollection(Article());

            var created = storage.Create("Article", new Dictionary<string, object?>
            {
                ["Title"] = "Проєктування СКБД",
                ["Year"] = 2026,
                ["Reviewed"] = false
            });

            id = created.Id;
            Assert.NotEqual(Ulid.Empty, id);
        }

        // Reopened, so what follows reads what was actually written to the file.
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var read = storage.GetById("Article", id);
            Assert.NotNull(read);
            Assert.Equal("Проєктування СКБД", read!.Fields["Title"]);
            Assert.Equal(2026, read.Fields["Year"]);
            Assert.Equal(false, read.Fields["Reviewed"]);

            Assert.True(storage.Update(new StorageRecord(id, "Article", new Dictionary<string, object?>
            {
                ["Title"] = "Проєктування СКБД, видання друге",
                ["Year"] = 2027,
                ["Reviewed"] = true
            })));
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var updated = Assert.Single(storage.GetAll("Article"));
            Assert.Equal(id, updated.Id);
            Assert.Equal("Проєктування СКБД, видання друге", updated.Fields["Title"]);
            Assert.Equal(2027, updated.Fields["Year"]);
            Assert.Equal(true, updated.Fields["Reviewed"]);

            Assert.True(storage.Delete("Article", id));
            Assert.False(storage.Delete("Article", id));
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            Assert.Empty(storage.GetAll("Article"));
            Assert.Null(storage.GetById("Article", id));
        }
    }

    [Fact]
    public void ACollectionDefinitionSurvivesAReopen()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            storage.CreateCollection(Article());
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var definition = storage.GetCollectionDefinition("Article");
            Assert.NotNull(definition);
            Assert.Equal("A publication record", definition!.Description);
            Assert.Equal(
                ["Title", "Year", "Reviewed"],
                definition.Columns.Select(column => column.Name).ToArray());
            Assert.Equal(
                [ColumnType.String, ColumnType.Int32, ColumnType.Boolean],
                definition.Columns.Select(column => column.Type).ToArray());
            Assert.Equal("The title as published", definition.Columns.First().Description);

            // The engine's own system collections stay on its side of the boundary.
            Assert.Equal(["Article"], storage.GetCollectionDefinitions().Select(item => item.Name).ToArray());
        }
    }

    /// <summary>
    /// The inverse of what this test asserted while the adapter was a skeleton: every member
    /// of IStorage now does its job. The shared contract suite says what each one does; this
    /// says that none of them declines to.
    /// </summary>
    [Fact]
    public void NoMemberOfIStorageDeclinesToWork()
    {
        using var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Article());
        storage.CreateCollection(new CollectionDefinition("Author",
            columns: [new ColumnDefinition("Code", ColumnType.String, unique: true)]));
        var definition = storage.GetCollectionDefinition("Article")!;
        var created = storage.Create("Article", new Dictionary<string, object?>
        {
            ["Title"] = "Проєктування СКБД", ["Year"] = 2026, ["Reviewed"] = false
        });

        var calls = new (string Member, Action Call)[]
        {
            ("GetCollectionDefinitions", () => storage.GetCollectionDefinitions()),
            ("AddColumn", () => storage.AddColumn("Article", new ColumnDefinition("Doi", ColumnType.String))),
            ("UpdateColumn", () => storage.UpdateColumn("Article", "Doi",
                new ColumnDefinition("Identifier", ColumnType.String))),
            ("RemoveColumn", () => storage.RemoveColumn("Article", "Identifier")),
            ("SetDisplayRule", () => storage.SetDisplayRule("Article", new DisplayRule("{Title}"))),
            //Related on a column the one stored record does not carry: a column that refers to
            //nothing is not a broken reference, so this exercises the member without the
            //referential check having anything to object to.
            ("AddColumn (relation source)", () =>
                storage.AddColumn("Article", new ColumnDefinition("AuthorCode", ColumnType.String))),
            ("AddRelation", () => storage.AddRelation(new RelationDefinition(
                "ArticleAuthor", RelationType.ManyToOne, "Article", "AuthorCode", "Author", "Code"))),
            ("GetRelation", () => storage.GetRelation("ArticleAuthor")),
            ("GetRelations", () => storage.GetRelations()),
            ("ExecuteQuery", () => storage.ExecuteQuery(new StorageQuery(definition, null, [], 0, 10, []))),
            ("GetAll", () => storage.GetAll("Article")),
            ("GetById", () => storage.GetById("Article", created.Id)),
            ("Update", () => storage.Update(new StorageRecord(created.Id, "Article", created.Fields))),
            ("Delete", () => storage.Delete("Article", created.Id)),
            ("RemoveRelation", () => storage.RemoveRelation("ArticleAuthor")),
            ("DeleteCollection", () => storage.DeleteCollection("Article"))
        };

        foreach (var (member, call) in calls)
        {
            var exception = Record.Exception(call);
            Assert.False(exception is NotSupportedException,
                $"{member} threw NotSupportedException: {exception?.Message}");
            Assert.Null(exception);
        }
    }

    /// <summary>
    /// Phase 7's exit condition, for the parts of it this adapter owns: the file is closed and
    /// reopened and the schema is still there — columns as they were last changed, the
    /// relation, the display rule and the collection's metadata.
    ///
    /// The contract suite cannot catch this. It uses one storage instance throughout, so a
    /// backend that kept all of this in a dictionary beside the file would pass every test in
    /// it.
    /// </summary>
    [Fact]
    public void TheWholeSchemaSurvivesClosingAndReopeningTheFile()
    {
        var articleId = Ulid.Empty;

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            storage.CreateCollection(new CollectionDefinition(
                "Article",
                "A publication record",
                [
                    new ColumnDefinition("Title", ColumnType.String, "The title as published"),
                    new ColumnDefinition("Year", ColumnType.Int32),
                    new ColumnDefinition("Reviewed", ColumnType.Boolean)
                ],
                new Dictionary<string, string?> { ["source"] = "crossref" }));
            storage.CreateCollection(new CollectionDefinition("Journal",
                columns: [new ColumnDefinition("Issn", ColumnType.String, unique: true)]));

            storage.AddColumn("Article", new ColumnDefinition("Issn", ColumnType.String, "Where it appeared"));
            storage.UpdateColumn("Article", "Reviewed",
                new ColumnDefinition("PeerReviewed", ColumnType.Boolean));
            storage.SetDisplayRule("Article", new DisplayRule("{Title} ({Year})"));
            storage.AddRelation(new RelationDefinition(
                "ArticleJournal", RelationType.ManyToOne, "Article", "Issn", "Journal", "Issn",
                "The journal an article appeared in"));

            storage.Create("Journal", new Dictionary<string, object?> { ["Issn"] = "0000-0019" });
            articleId = storage.Create("Article", new Dictionary<string, object?>
            {
                ["Title"] = "Проєктування СКБД",
                ["Year"] = 2026,
                ["PeerReviewed"] = true,
                ["Issn"] = "0000-0019"
            }).Id;
        }

        using (var reopened = new TokkDbStorage(_databaseFilePath))
        {
            var definition = reopened.GetCollectionDefinition("Article");
            Assert.NotNull(definition);
            Assert.Equal("A publication record", definition!.Description);
            Assert.Equal(["Title", "Year", "PeerReviewed", "Issn"],
                definition.Columns.Select(column => column.Name).ToArray());
            Assert.Equal("The title as published",
                definition.Columns.First(column => column.Name == "Title").Description);
            Assert.Equal("crossref", definition.Metadata["source"]);
            Assert.Equal("{Title} ({Year})", definition.DisplayRule?.Template);

            var relation = reopened.GetRelation("ArticleJournal");
            Assert.NotNull(relation);
            Assert.Equal(RelationType.ManyToOne, relation!.Type);
            Assert.Equal("The journal an article appeared in", relation.Description);
            Assert.Equal("Journal", relation.TargetCollection);

            var article = reopened.GetById("Article", articleId);
            Assert.NotNull(article);
            Assert.Equal("Проєктування СКБД", article!.Fields["Title"]);
            //Renamed before the reopen: the value came across with the name.
            Assert.Equal(true, article.Fields["PeerReviewed"]);
            Assert.False(article.Fields.ContainsKey("Reviewed"));
        }
    }

    /// <summary>
    /// D-2, as a property of the type rather than of one code path: the definition the agent
    /// tools reason about carries the five logical things and nothing physical. The engine's
    /// descriptor for the same collection also holds the data chain, the index roots, the
    /// free-space root and the record count, and a tool that could see any of them would be
    /// reasoning about storage layout it has no business knowing.
    /// </summary>
    [Fact]
    public void TheDefinitionTheAgentToolsSeeCarriesNothingPhysical()
    {
        var properties = typeof(CollectionDefinition)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(["Columns", "Description", "DisplayRule", "Metadata", "Name"], properties);
    }

    /// <summary>
    /// DC-5: ExecuteQuery runs through the planner rather than filtering a full read. The
    /// answer would be the same either way, so what is asserted is the access path — which is
    /// the only thing that distinguishes the two.
    /// </summary>
    [Fact]
    public void ExecuteQueryReachesItsRecordsThroughThePlanner()
    {
        var reports = new List<QueryReport>();
        using var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Article());
        for (var i = 0; i < 200; i++)
        {
            storage.Create("Article", new Dictionary<string, object?>
            {
                ["Title"] = $"Article {i}", ["Year"] = 2000 + i % 20, ["Reviewed"] = i % 2 == 0
            });
        }

        storage.CreateIndex("Article", "Year");
        storage.Queries.QueryExecuted += reports.Add;

        var definition = storage.GetCollectionDefinition("Article")!;
        var year = definition.Columns.First(column => column.Name == "Year");
        var result = storage.ExecuteQuery(new StorageQuery(
            definition,
            new StorageFieldFilter(year, QueryOperator.Equals, ["2005"]),
            [],
            0,
            100,
            []));

        Assert.Equal(10, result.Rows.Count);
        var report = Assert.Single(reports);
        Assert.Equal("index seek on Article.Year", report.AccessPath);
        //Ten records read out of two hundred, and only those ten turned into documents.
        Assert.Equal(10, report.RecordsExamined);
        Assert.Equal(10, report.DocumentsMaterialised);
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
