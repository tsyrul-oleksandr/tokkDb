using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb;
using TokkDb.Disk;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// What the contract suite cannot ask, because it holds one storage for the length of one test:
/// whether any of this survives being written to a file and read back out of it.
///
/// That is the whole difference between this implementation and the in-memory one, so a suite
/// that passes against both and never closes a file has not tested the part that is different.
/// Everything here opens a database, closes it, opens it again, and looks.
/// </summary>
public sealed class TokkDbStorageTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("reopen");

    private string _path => _database.FilePath;

    public void Dispose() => _database.Dispose();

    private TokkDbStorage Open() => new(_path);

    /// <summary>
    /// The definition, exactly as it was written, after the file has been closed and opened
    /// again. Two of the things in it - which columns have to have a value, and which dates are
    /// dates rather than moments - have no room in the engine's column descriptor and are kept
    /// in the collection's settings document, so this is what says that encoding works.
    /// </summary>
    [Fact]
    public void A_definition_survives_being_closed_and_opened_again()
    {
        var written = new CollectionDefinition(
            "expenses",
            "money I spent and want to remember",
            columns:
            [
                new ColumnDefinition("event", ColumnType.Text, "what it was for", required: true),
                new ColumnDefinition("amount_eur", ColumnType.Decimal, "what it came to", required: true),
                new ColumnDefinition("paid_on", ColumnType.Date, "the day it was paid"),
                new ColumnDefinition("seen_at", ColumnType.Timestamp, "when I was told"),
                new ColumnDefinition("reimbursed", ColumnType.Boolean),
                new ColumnDefinition("nights", ColumnType.Integer),
                new ColumnDefinition("receipt_no", ColumnType.Text, unique: true),
                new ColumnDefinition("came_from", ColumnType.Text, readOnly: true),
                new ColumnDefinition("currency", ColumnType.Text, defaultValue: "EUR")
            ],
            metadata: new Dictionary<string, string?>
            {
                ["source"] = "a spreadsheet",
                ["last_import"] = "",
                ["never_set"] = null
            },
            displayRule: new DisplayRule("{event} on {paid_on}"));

        using (var storage = Open())
        {
            storage.CreateCollection(written);
        }

        using var reopened = Open();
        var read = reopened.GetCollectionDefinition("expenses");

        Assert.Equal(written, read);
    }

    /// <summary>
    /// SC-3 across the file boundary. A value that is written as itself and read back as
    /// something else is the failure the requirement exists to prevent, and it is invisible
    /// until the process restarts.
    /// </summary>
    [Fact]
    public void Every_column_type_survives_being_closed_and_opened_again()
    {
        var paidOn = new DateOnly(2026, 7, 20);
        var seenAt = new DateTime(2026, 7, 21, 9, 30, 0, DateTimeKind.Utc);
        Ulid id;

        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            id = storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["amount_eur"] = 840.50m,
                ["paid_on"] = paidOn,
                ["seen_at"] = seenAt,
                ["reimbursed"] = false,
                ["nights"] = 4L
            }).Id;
        }

        using var reopened = Open();
        var record = reopened.GetById("expenses", id)!;

        Assert.Equal("EuroPython", Assert.IsType<string>(record["event"]));
        Assert.Equal(840.50m, Assert.IsType<decimal>(record["amount_eur"]));
        Assert.Equal(paidOn, Assert.IsType<DateOnly>(record["paid_on"]));
        Assert.Equal(seenAt, Assert.IsType<DateTime>(record["seen_at"]));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)record["seen_at"]!).Kind);
        Assert.False(Assert.IsType<bool>(record["reimbursed"]));
        Assert.Equal(4L, Assert.IsType<long>(record["nights"]));
        Assert.Equal("EUR", record["currency"]);
    }

    /// <summary>
    /// A column given nothing and a column never given anything are different, and they have to
    /// stay different on disk. In a document one is a null and the other is not there at all.
    /// </summary>
    [Fact]
    public void The_difference_between_nothing_and_not_there_survives_a_reopen()
    {
        Ulid id;

        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            id = storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["amount_eur"] = 840m,
                ["nights"] = null
            }).Id;
        }

        using var reopened = Open();
        var record = reopened.GetById("expenses", id)!;

        Assert.True(record.Has("nights"));
        Assert.Null(record["nights"]);
        Assert.False(record.Has("seen_at"));
    }

    [Fact]
    public void Records_survive_being_closed_and_opened_again()
    {
        Ulid[] written;

        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            written = Enumerable.Range(0, 25)
                .Select(i => storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["event"] = $"event {i}",
                    ["amount_eur"] = 100m + i
                }).Id)
                .ToArray();
        }

        using var reopened = Open();
        var read = reopened.GetAll("expenses");

        Assert.Equal(25, read.Count);
        Assert.Equal(written.Order(), read.Select(static record => record.Id).Order());
    }

    /// <summary>
    /// The unit of work is the engine's transaction, so a failure inside one has to be absent
    /// from the file and not merely from the objects in memory.
    /// </summary>
    [Fact]
    public void Work_that_threw_is_not_in_the_file_afterwards()
    {
        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "before",
                ["amount_eur"] = 1m
            });

            Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
            {
                for (var i = 0; i < 20; i++)
                {
                    storage.Create("expenses", new Dictionary<string, object?>
                    {
                        ["event"] = $"during {i}",
                        ["amount_eur"] = 2m
                    });
                }

                throw new InvalidOperationException("the import ran out half way through");
            }));
        }

        using var reopened = Open();
        var left = Assert.Single(reopened.GetAll("expenses"));
        Assert.Equal("before", left["event"]);
    }

    /// <summary>
    /// A collection created inside a unit of work that failed must not be in the catalogue on
    /// disk either. The engine reloads its catalogues after an outermost rollback for exactly
    /// this reason, and this is what says the adapter benefits from it.
    /// </summary>
    [Fact]
    public void A_collection_created_inside_work_that_threw_is_not_in_the_file_afterwards()
    {
        using (var storage = Open())
        {
            Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
            {
                storage.CreateCollection(Expenses());
                throw new InvalidOperationException("no");
            }));

            Assert.Null(storage.GetCollectionDefinition("expenses"));
        }

        using var reopened = Open();
        Assert.Null(reopened.GetCollectionDefinition("expenses"));
    }

    [Fact]
    public void A_deleted_collection_is_gone_from_the_file_and_takes_its_settings_with_it()
    {
        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["amount_eur"] = 840m
            });

            Assert.True(storage.DeleteCollection("expenses"));
        }

        using var reopened = Open();
        Assert.Null(reopened.GetCollectionDefinition("expenses"));

        // The name is free, and nothing the old definition said comes back with it - which is
        // the thing to check, because the required columns and the dates live in a settings
        // document rather than in the descriptor.
        reopened.CreateCollection(new CollectionDefinition("expenses", columns:
            [new ColumnDefinition("title", ColumnType.Text)]));

        var fresh = reopened.GetCollectionDefinition("expenses")!;
        Assert.Equal(["title"], fresh.Columns.Select(static column => column.Name));
        Assert.Empty(fresh.Metadata);
        Assert.Empty(reopened.GetAll("expenses"));
    }

    /// <summary>
    /// The engine's own reserved collections are the catalogue, the indexes, the settings and
    /// the rest. They are collections, and a storage that listed them would put the database's
    /// own bookkeeping in front of the user in the browser of BR-1.
    /// </summary>
    [Fact]
    public void The_engines_own_collections_are_not_things_the_user_has_stored()
    {
        using var storage = Open();
        storage.CreateCollection(Expenses());

        Assert.Equal(["expenses"], storage.GetCollectionDefinitions().Select(static d => d.Name));

        // And they cannot be asked for by name either: the engine marks its own with a leading
        // underscore, which the contract's naming rule does not admit, so a reserved name is
        // refused as unusable rather than answered as missing.
        foreach (var reserved in new[] { "_collections", "_indexes", "_settings", "_displayRules" })
        {
            Assert.Throws<InvalidDefinitionException>(() => storage.GetCollectionDefinition(reserved));
        }
    }

    private static CollectionDefinition Expenses() => new(
        "expenses",
        columns:
        [
            new ColumnDefinition("event", ColumnType.Text, required: true),
            new ColumnDefinition("amount_eur", ColumnType.Decimal, required: true),
            new ColumnDefinition("paid_on", ColumnType.Date),
            new ColumnDefinition("seen_at", ColumnType.Timestamp),
            new ColumnDefinition("reimbursed", ColumnType.Boolean),
            new ColumnDefinition("nights", ColumnType.Integer),
            new ColumnDefinition("receipt_no", ColumnType.Text, unique: true),
            new ColumnDefinition("currency", ColumnType.Text, defaultValue: "EUR")
        ]);

    // =============================================================================================
    // The two claims the contract suite cannot check, because both are about what the engine did
    // rather than about what a read returns.
    // =============================================================================================

    /// <summary>
    /// SC-5's acceptance condition, counted rather than assumed. The engine commits a transaction
    /// by marking the journal, and that is a virtual method, so a disk manager that counts the
    /// marks says exactly how many commits an import paid for.
    /// </summary>
    [Fact]
    public void An_import_of_five_hundred_records_is_one_commit()
    {
        using var disk = new CountingDiskManager(_path);
        using var connection = new TokkDbConnection(disk);
        connection.Load();

        var storage = new TokkDbStorage(connection);
        storage.CreateCollection(Expenses());

        var before = disk.Commits;

        storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["event"] = $"row {i}",
                    ["amount_eur"] = 10m + i,
                    ["receipt_no"] = $"R-{i:0000}"
                });
            }
        });

        Assert.Equal(1, disk.Commits - before);
        Assert.Equal(500, storage.GetAll("expenses").Count);
    }

    /// <summary>
    /// The other half of the same measurement, without which the one above proves only that the
    /// counter does not move. Five hundred records outside a unit of work are five hundred
    /// commits, and every one of them pays the commit protocol.
    /// </summary>
    [Fact]
    public void The_same_five_hundred_records_without_a_unit_of_work_are_five_hundred_commits()
    {
        using var disk = new CountingDiskManager(_path);
        using var connection = new TokkDbConnection(disk);
        connection.Load();

        var storage = new TokkDbStorage(connection);
        storage.CreateCollection(Expenses());

        var before = disk.Commits;

        for (var i = 0; i < 500; i++)
        {
            storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = $"row {i}",
                ["amount_eur"] = 10m + i,
                ["receipt_no"] = $"R-{i:0000}"
            });
        }

        Assert.Equal(500, disk.Commits - before);
    }

    [Fact]
    public void An_import_that_fails_half_way_leaves_nothing_in_the_file()
    {
        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());

            Assert.Throws<StorageValidationException>(() => storage.InUnitOfWork(() =>
            {
                for (var i = 0; i < 400; i++)
                {
                    storage.Create("expenses", new Dictionary<string, object?>
                    {
                        ["event"] = $"row {i}",
                        ["amount_eur"] = 10m + i,
                        ["receipt_no"] = $"R-{i:0000}"
                    });
                }

                storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["event"] = "the row that repeats a receipt number",
                    ["amount_eur"] = 1m,
                    ["receipt_no"] = "R-0000"
                });
            }));
        }

        using var reopened = Open();
        Assert.Empty(reopened.GetAll("expenses"));
    }

    /// <summary>
    /// SC-6 says the change goes underneath the engine's lazy migration, and the contract suite
    /// cannot tell lazy from eager - both serve the same values. What tells them apart is that
    /// converging afterwards has work to do: if the retype had rewritten the records, there
    /// would be nothing left to rewrite.
    /// </summary>
    [Fact]
    public void A_retype_does_not_rewrite_the_records_and_converging_is_what_does()
    {
        using var storage = Open();
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text),
            new ColumnDefinition("value", ColumnType.Text)
        ]));

        for (var i = 0; i < 30; i++)
        {
            storage.Create("readings", new Dictionary<string, object?>
            {
                ["label"] = $"reading {i}",
                ["value"] = (100 + i).ToString()
            });
        }

        storage.RetypeColumn("readings", "value", ColumnType.Integer);

        // Read correctly, and still on the older side of the change.
        Assert.Equal(100L, storage.GetAll("readings").OrderBy(static r => r.Id).First()["value"]);

        Assert.Equal(30, storage.Converge("readings"));
        Assert.Equal(0, storage.Converge("readings"));

        // And the same answers afterwards, with nothing left to replay.
        Assert.Equal(
            Enumerable.Range(100, 30).Select(static value => (object)(long)value).ToArray(),
            storage.GetAll("readings").OrderBy(static r => r.Id).Select(static r => r["value"]).ToArray());
    }

    [Fact]
    public void A_structural_change_survives_being_closed_and_opened_again()
    {
        Ulid before;

        using (var storage = Open())
        {
            storage.CreateCollection(Expenses());
            before = storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["amount_eur"] = 840m,
                ["nights"] = 4L
            }).Id;

            storage.RenameColumn("expenses", "nights", "nights_away");
            storage.RetypeColumn("expenses", "nights_away", ColumnType.Text);
            storage.AddColumn("expenses", new ColumnDefinition("venue", ColumnType.Text, required: false));
            storage.RemoveColumn("expenses", "reimbursed");
        }

        using var reopened = Open();
        var definition = reopened.GetCollectionDefinition("expenses")!;

        Assert.Null(definition.Column("nights"));
        Assert.Null(definition.Column("reimbursed"));
        Assert.Equal(ColumnType.Text, definition.Column("nights_away")!.Type);
        Assert.NotNull(definition.Column("venue"));

        // The value written as a whole number, under a different name, before the retype.
        Assert.Equal("4", reopened.GetById("expenses", before)!["nights_away"]);
    }

    /// <summary>
    /// The two facts the engine's descriptor has no room for - which columns have to have a
    /// value, and which dates are dates - live in the collection's settings document, and a
    /// structural change rewrites that document. This is what says the rewrite keeps them.
    /// </summary>
    [Fact]
    public void What_the_settings_document_carries_survives_a_structural_change()
    {
        using var storage = Open();
        storage.CreateCollection(Expenses());

        storage.AddColumn("expenses", new ColumnDefinition("venue", ColumnType.Text));

        var definition = storage.GetCollectionDefinition("expenses")!;
        Assert.True(definition.Column("event")!.Required);
        Assert.True(definition.Column("amount_eur")!.Required);
        Assert.Equal(ColumnType.Date, definition.Column("paid_on")!.Type);
        Assert.Equal(ColumnType.Timestamp, definition.Column("seen_at")!.Type);

        // And after a rename, under the new name.
        storage.RenameColumn("expenses", "paid_on", "settled_on");
        Assert.Equal(ColumnType.Date, storage.GetCollectionDefinition("expenses")!.Column("settled_on")!.Type);
    }

    /// <summary>Counts what the engine calls a commit: one mark on the journal per committed transaction.</summary>
    private sealed class CountingDiskManager(string filePath) : DiskManager(filePath)
    {
        public int Commits { get; private set; }

        public override void CommitJournal(ulong transactionId)
        {
            Commits++;
            base.CommitJournal(transactionId);
        }
    }

    /// <summary>
    /// SC-7's acceptance condition, and the claim underneath it. A seek is only worth reporting
    /// if it is worth taking, and the measure of that is pages: the same question asked of an
    /// indexed column and an unindexed one, over the same records, in the same file.
    /// </summary>
    [Fact]
    public void A_seek_reads_a_fraction_of_the_pages_a_scan_reads()
    {
        using var storage = Open();
        storage.CreateCollection(Expenses());

        storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["event"] = $"event {i:0000}",
                    ["amount_eur"] = 10m + i,
                    ["receipt_no"] = $"R-{i:0000}"
                });
            }
        });

        // receipt_no is unique, so it has an index; event is not, so it has none.
        var seek = storage.ExecuteQuery(new StorageQuery("expenses",
            [new QueryCondition("receipt_no", QueryOperator.Equals, "R-0631")]));

        var scan = storage.ExecuteQuery(new StorageQuery("expenses",
            [new QueryCondition("event", QueryOperator.Equals, "event 0631")]));

        Assert.Equal(QueryAccessPathKind.IndexSeek, seek.AccessPath.Kind);
        Assert.Equal("receipt_no", seek.AccessPath.ColumnName);
        Assert.Equal(QueryAccessPathKind.FullScan, scan.AccessPath.Kind);

        // Both find the one record they were asked for.
        Assert.Equal("event 0631", Assert.Single(seek.Records)["event"]);
        Assert.Equal("R-0631", Assert.Single(scan.Records)["receipt_no"]);

        // The seek looks at a handful of records; the scan looks at all thousand.
        Assert.True(
            seek.Cost.RecordsExamined < 10,
            $"the seek examined {seek.Cost.RecordsExamined} records");
        Assert.Equal(1_000, scan.Cost.RecordsExamined);

        Assert.True(
            seek.Cost.PagesRead * 4 < scan.Cost.PagesRead,
            $"the seek read {seek.Cost.PagesRead} pages against the scan's {scan.Cost.PagesRead}");
    }

    /// <summary>
    /// A query is validated against the schema before anything is read, so a query that does not
    /// fit costs no page reads at all - which is the difference between refusing a model's bad
    /// query and running it.
    /// </summary>
    [Fact]
    public void A_query_that_does_not_fit_the_schema_reads_no_pages()
    {
        using var storage = Open();
        storage.CreateCollection(Expenses());

        storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["event"] = $"event {i}", ["amount_eur"] = 1m
                });
            }
        });

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery("expenses", [new QueryCondition("venue", QueryOperator.Equals, "Prague")])));

        Assert.Single(thrown.Errors);
    }
}
