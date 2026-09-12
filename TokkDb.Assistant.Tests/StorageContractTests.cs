using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The contract suite. Every implementation of <see cref="IStorage"/> runs it, and the only
/// thing an implementation supplies is <see cref="NewStorage"/>.
///
/// It exists because of the finding §2.2 of the engine plan recorded: two storage backends
/// disagree about what the same schema means, on the questions nobody wrote down. SC-4 wrote
/// five of them down and the contract's documentation settled nine. This is where the settlement
/// is enforced rather than asserted - the sections below are the five SC-4 names, plus the
/// collection lifecycle they all need and the unit of work SC-5 asked for in the first version.
///
/// Two rules for anything added here:
///
/// <list type="bullet">
/// <item>
/// <b>Nothing may depend on the order of <see cref="IStorage.GetAll"/>.</b> That is answer five,
/// and a suite that quietly relied on an order would be the thing it is meant to catch.
/// <see cref="MemoryStorage"/> shuffles on purpose so that a slip fails here.
/// </item>
/// <item>
/// <b>Nothing may reach past the interface.</b> No cast to a concrete storage, no file on disk,
/// no dictionary. A test that needs to know which implementation it is running against is
/// testing an implementation and belongs beside it.
/// </item>
/// </list>
/// </summary>
public abstract class StorageContractTests : IDisposable
{
    private readonly List<IStorage> _created = [];

    private IStorage? _storage;

    /// <summary>The one thing an implementation has to supply: an empty storage.</summary>
    protected abstract IStorage NewStorage();

    /// <summary>
    /// Whether this storage has indexes to seek. The in-memory one does not and cannot pretend
    /// to; the engine-backed one does, and SC-7's acceptance condition is about it.
    ///
    /// A capability the suite knows about, rather than a test the in-memory implementation
    /// quietly skips: both are asserted below, each against what its storage can honestly do, so
    /// an implementation that stopped using its indexes would fail here rather than pass by
    /// default.
    /// </summary>
    protected virtual bool SeeksIndexes => false;

    /// <summary>The storage under test. One per test, because xUnit builds the class per test.</summary>
    protected IStorage Storage => _storage ??= Track(NewStorage());

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Closes everything the test opened. An implementation that also has something outside the
    /// contract to tidy up - a file, a directory - overrides this and calls the base first,
    /// because a database file cannot be deleted until its connection has let go of it.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        foreach (var storage in _created)
        {
            (storage as IDisposable)?.Dispose();
        }

        _created.Clear();
    }

    /// <summary>An implementation that holds a file or a connection is closed when the test ends.</summary>
    protected IStorage Track(IStorage storage)
    {
        _created.Add(storage);
        return storage;
    }

    // =============================================================================================
    // The collection a great deal of the suite writes into. It carries one column of every shape
    // the contract distinguishes, so that most tests are one line against a shared fixture rather
    // than five lines of their own scaffolding.
    // =============================================================================================

    private const string Expenses = "expenses";

    private static CollectionDefinition ExpensesDefinition() => new(
        Expenses,
        "money I spent and want to remember",
        columns:
        [
            new ColumnDefinition("event", ColumnType.Text, "what the money was for", required: true),
            new ColumnDefinition("city", ColumnType.Text, "where it happened"),
            new ColumnDefinition("amount_eur", ColumnType.Decimal, "what it came to", required: true),
            new ColumnDefinition("paid_on", ColumnType.Date, "the day it was paid"),
            new ColumnDefinition("seen_at", ColumnType.Timestamp, "when the assistant was told"),
            new ColumnDefinition("reimbursed", ColumnType.Boolean, "whether it came back"),
            new ColumnDefinition("nights", ColumnType.Integer, "nights away"),
            new ColumnDefinition("receipt_no", ColumnType.Text, "the receipt's own number", unique: true),
            new ColumnDefinition("came_from", ColumnType.Text, "where the data came from", readOnly: true),
            new ColumnDefinition("currency", ColumnType.Text, "the currency it was paid in", defaultValue: "EUR")
        ],
        displayRule: new DisplayRule("{event} in {city}"));

    private static Dictionary<string, object?> AnExpense(
        string @event = "EuroPython",
        string? receipt = null) =>
        new()
        {
            ["event"] = @event,
            ["city"] = "Prague",
            ["amount_eur"] = 840.50m,
            ["receipt_no"] = receipt
        };

    private IStorage GivenExpenses()
    {
        Storage.CreateCollection(ExpensesDefinition());
        return Storage;
    }

    // =============================================================================================
    // Collections. Not one of the five, but everything else needs it and a storage that got this
    // wrong would fail the five for the wrong reason.
    // =============================================================================================

    [Fact]
    public void A_collection_that_was_created_reads_back_as_the_definition_that_was_written()
    {
        var written = ExpensesDefinition();
        Storage.CreateCollection(written);

        Assert.Equal(written, Storage.GetCollectionDefinition(Expenses));
    }

    [Fact]
    public void There_is_no_definition_for_a_collection_that_was_never_created()
    {
        Assert.Null(Storage.GetCollectionDefinition("nothing_like_this"));
    }

    [Fact]
    public void A_collection_cannot_be_created_twice()
    {
        Storage.CreateCollection(ExpensesDefinition());

        var thrown = Assert.Throws<CollectionAlreadyExistsException>(
            () => Storage.CreateCollection(ExpensesDefinition()));

        Assert.Equal(Expenses, thrown.CollectionName);
    }

    [Fact]
    public void Every_collection_is_listed()
    {
        Storage.CreateCollection(ExpensesDefinition());
        Storage.CreateCollection(new CollectionDefinition("reading_list", columns:
            [new ColumnDefinition("title", ColumnType.Text)]));

        Assert.Equal(
            ["expenses", "reading_list"],
            Storage.GetCollectionDefinitions().Select(static d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_definition_whose_display_rule_names_a_column_it_does_not_have_is_refused()
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(() => Storage.CreateCollection(
            new CollectionDefinition(
                Expenses,
                columns: [new ColumnDefinition("event", ColumnType.Text)],
                displayRule: new DisplayRule("{event} in {city}"))));

        Assert.Equal("display rule", thrown.Member);
        Assert.Null(Storage.GetCollectionDefinition(Expenses));
    }

    [Fact]
    public void Deleting_a_collection_takes_its_records_with_it()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense());

        Assert.True(storage.DeleteCollection(Expenses));
        Assert.Null(storage.GetCollectionDefinition(Expenses));
        Assert.Throws<UnknownCollectionException>(() => storage.GetAll(Expenses));

        // And the name is free again, with nothing left in it.
        storage.CreateCollection(ExpensesDefinition());
        Assert.Empty(storage.GetAll(Expenses));
    }

    [Fact]
    public void Deleting_a_collection_that_is_not_there_says_so_rather_than_throwing()
    {
        Assert.False(Storage.DeleteCollection("nothing_like_this"));
    }

    [Fact]
    public void Every_operation_on_a_collection_that_is_not_there_says_which_collection()
    {
        var id = Ulid.NewUlid();

        foreach (var operation in new Action[]
                 {
                     () => Storage.Create("nothing_like_this", AnExpense()),
                     () => Storage.GetById("nothing_like_this", id),
                     () => Storage.Update(new StorageRecord(id, "nothing_like_this", AnExpense())),
                     () => Storage.Delete("nothing_like_this", id),
                     () => Storage.GetAll("nothing_like_this")
                 })
        {
            var thrown = Assert.Throws<UnknownCollectionException>(operation);
            Assert.Equal("nothing_like_this", thrown.CollectionName);
        }
    }

    // =============================================================================================
    // SC-4, answer 4: names are trimmed when they are created and compared ordinally.
    // =============================================================================================

    [Fact]
    public void A_collection_created_with_whitespace_around_its_name_is_found_without_it()
    {
        Storage.CreateCollection(new CollectionDefinition("  expenses  ", columns:
            [new ColumnDefinition("event", ColumnType.Text)]));

        Assert.NotNull(Storage.GetCollectionDefinition(Expenses));
        Assert.Equal(Expenses, Storage.GetCollectionDefinition(Expenses)!.Name);

        // And the trimmed name is taken, so the same name with other whitespace is the same name.
        Assert.Throws<CollectionAlreadyExistsException>(() =>
            Storage.CreateCollection(new CollectionDefinition("expenses\t", columns:
                [new ColumnDefinition("event", ColumnType.Text)])));
    }

    [Fact]
    public void A_collection_name_in_another_case_is_another_collection()
    {
        var storage = GivenExpenses();

        Assert.Null(storage.GetCollectionDefinition("Expenses"));
        Assert.Throws<UnknownCollectionException>(() => storage.GetAll("EXPENSES"));

        // Which means both can exist, and they are two things.
        storage.CreateCollection(new CollectionDefinition("Expenses", columns:
            [new ColumnDefinition("title", ColumnType.Text)]));

        Assert.Equal(2, storage.GetCollectionDefinitions().Count);
        Assert.Equal("event", storage.GetCollectionDefinition(Expenses)!.Columns[0].Name);
        Assert.Equal("title", storage.GetCollectionDefinition("Expenses")!.Columns[0].Name);
    }

    /// <summary>
    /// A name that is not a name is a different answer from a name that is not there. Reporting
    /// the first as the second would tell a caller to create it, and the creation would be
    /// refused for the reason the lookup should have given in the first place.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9lives")]
    [InlineData("has space")]
    [InlineData("_reserved")]
    public void A_name_that_is_not_a_name_is_refused_rather_than_reported_as_missing(string name)
    {
        Assert.Throws<InvalidDefinitionException>(() => Storage.GetCollectionDefinition(name));
        Assert.Throws<InvalidDefinitionException>(() => Storage.GetAll(name));
        Assert.Throws<InvalidDefinitionException>(() => Storage.DeleteCollection(name));
    }

    [Fact]
    public void A_column_name_in_another_case_is_a_column_the_collection_does_not_have()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?>
            {
                ["Event"] = "EuroPython",
                ["amount_eur"] = 840m
            }));

        var unknown = Assert.Single(thrown.Errors.OfType<UnknownColumn>());
        Assert.Equal("Event", unknown.ColumnName);
    }

    // =============================================================================================
    // SC-4, answer 1: identity is a Ulid, the storage assigns it, and it is not a column.
    // =============================================================================================

    [Fact]
    public void Creating_a_record_returns_it_with_the_identity_the_storage_gave_it()
    {
        var record = GivenExpenses().Create(Expenses, AnExpense());

        Assert.NotEqual(default, record.Id);
        Assert.Equal(Expenses, record.CollectionName);
        Assert.Equal(record, Storage.GetById(Expenses, record.Id));
    }

    [Fact]
    public void Two_records_with_the_same_values_are_two_records()
    {
        var storage = GivenExpenses();

        var first = storage.Create(Expenses, AnExpense());
        var second = storage.Create(Expenses, AnExpense());

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, storage.GetAll(Expenses).Count);
    }

    /// <summary>
    /// The promise that replaces the one <see cref="IStorage.GetAll"/> does not make. If this
    /// fails, "show me what I just imported, in order" has no answer that does not require
    /// storing a row number - which SC-2 would not allow in a definition.
    /// </summary>
    [Fact]
    public void Identities_increase_so_sorting_by_identity_gives_the_order_they_were_created_in()
    {
        var storage = GivenExpenses();

        var created = Enumerable.Range(0, 50)
            .Select(i => storage.Create(Expenses, AnExpense($"event {i}")).Id)
            .ToArray();

        Assert.Equal(created, created.Order());
        Assert.Equal(created.Length, created.Distinct().Count());
    }

    [Fact]
    public void There_is_no_record_for_an_identity_that_was_never_issued()
    {
        Assert.Null(GivenExpenses().GetById(Expenses, Ulid.NewUlid()));
    }

    [Fact]
    public void The_identity_is_not_one_of_the_values()
    {
        var record = GivenExpenses().Create(Expenses, AnExpense());

        // Nothing in the values is the identity, under any spelling, and nothing in the values
        // is anything but a column of the collection.
        Assert.DoesNotContain("id", record.Fields.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(record.Id.ToString(), record.Fields.Values.Select(static value => value?.ToString()));

        var columns = ExpensesDefinition().Columns.Select(static column => column.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(record.Fields.Keys, key => Assert.Contains(key, columns));
    }

    /// <summary>
    /// Nothing is reserved. A collection may have a column called <c>id</c>, and it is an
    /// ordinary column with no relation to the identity.
    /// </summary>
    [Fact]
    public void A_column_called_id_is_an_ordinary_column()
    {
        Storage.CreateCollection(new CollectionDefinition("invoices", columns:
        [
            new ColumnDefinition("id", ColumnType.Text, "the number the supplier printed on it")
        ]));

        var record = Storage.Create("invoices", new Dictionary<string, object?> { ["id"] = "INV-4417" });

        Assert.Equal("INV-4417", record["id"]);
        Assert.NotEqual("INV-4417", record.Id.ToString());
    }

    [Fact]
    public void A_record_that_was_deleted_is_gone_and_deleting_it_again_says_so()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        Assert.True(storage.Delete(Expenses, record.Id));
        Assert.Null(storage.GetById(Expenses, record.Id));
        Assert.False(storage.Delete(Expenses, record.Id));
        Assert.Empty(storage.GetAll(Expenses));
    }

    // =============================================================================================
    // SC-4, answer 2 and SC-3: validation happens on write, and a value is stored as itself.
    // =============================================================================================

    [Fact]
    public void A_value_of_the_wrong_type_is_refused_and_names_the_column_and_the_type_it_wanted()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["amount_eur"] = "eight hundred and forty"
            }));

        var mismatch = Assert.Single(thrown.Errors.OfType<ColumnTypeMismatch>());
        Assert.Equal("amount_eur", mismatch.ColumnName);
        Assert.Equal(Expenses, mismatch.CollectionName);
        Assert.Equal(ColumnType.Decimal, mismatch.Expected);
        Assert.Contains("amount_eur", mismatch.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reason_a_write_was_refused_is_reported_not_only_the_first()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?>
            {
                ["amount_eur"] = true,
                ["venue"] = "Prague Congress Centre",
                ["nights"] = 4.5m
            }));

        Assert.Contains(thrown.Errors, static error => error is RequiredValueMissing { ColumnName: "event" });
        Assert.Contains(thrown.Errors, static error => error is ColumnTypeMismatch { ColumnName: "amount_eur" });
        Assert.Contains(thrown.Errors, static error => error is UnknownColumn { ColumnName: "venue" });
        Assert.Contains(thrown.Errors, static error => error is ColumnTypeMismatch { ColumnName: "nights" });
    }

    [Fact]
    public void A_refused_create_leaves_nothing_behind()
    {
        var storage = GivenExpenses();

        Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?> { ["amount_eur"] = "not a number" }));

        Assert.Empty(storage.GetAll(Expenses));
    }

    [Fact]
    public void A_refused_update_leaves_the_record_as_it_was()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        Assert.Throws<StorageValidationException>(() => storage.Update(record.With("amount_eur", "nothing")));

        Assert.Equal(record, storage.GetById(Expenses, record.Id));
    }

    [Fact]
    public void A_required_column_left_out_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?> { ["event"] = "EuroPython" }));

        var missing = Assert.Single(thrown.Errors.OfType<RequiredValueMissing>());
        Assert.Equal("amount_eur", missing.ColumnName);
    }

    /// <summary>
    /// Given nothing is not the same as left out, and a required column cannot be given nothing.
    /// </summary>
    [Fact]
    public void A_required_column_given_nothing_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?> { ["event"] = "EuroPython", ["amount_eur"] = null }));

        Assert.Single(thrown.Errors.OfType<RequiredValueMissing>());
    }

    [Fact]
    public void A_column_that_is_not_required_and_was_left_out_has_no_value()
    {
        var record = GivenExpenses().Create(Expenses, AnExpense());

        Assert.True(record.Has("city"));
        Assert.False(record.Has("nights"));
        Assert.Null(record["nights"]);
    }

    [Fact]
    public void A_default_fills_a_column_left_out_when_the_record_is_created()
    {
        var record = GivenExpenses().Create(Expenses, AnExpense());

        Assert.Equal("EUR", record["currency"]);
    }

    [Fact]
    public void A_default_does_not_come_back_when_the_record_is_updated()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        Assert.True(storage.Update(record.Without("currency")));

        var after = storage.GetById(Expenses, record.Id)!;
        Assert.False(after.Has("currency"));
        Assert.Null(after["currency"]);
    }

    /// <summary>
    /// SC-3, said as plainly as it can be said: what went in is what comes out, as the type it
    /// went in as. Nothing here parses a string.
    /// </summary>
    [Fact]
    public void Every_column_type_comes_back_as_itself()
    {
        var storage = GivenExpenses();
        var paidOn = new DateOnly(2026, 7, 20);
        var seenAt = new DateTime(2026, 7, 21, 9, 30, 0, DateTimeKind.Utc);

        var written = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython",
            ["amount_eur"] = 840.50m,
            ["paid_on"] = paidOn,
            ["seen_at"] = seenAt,
            ["reimbursed"] = false,
            ["nights"] = 4L
        });

        var read = storage.GetById(Expenses, written.Id)!;

        Assert.Equal("EuroPython", Assert.IsType<string>(read["event"]));
        Assert.Equal(840.50m, Assert.IsType<decimal>(read["amount_eur"]));
        Assert.Equal(paidOn, Assert.IsType<DateOnly>(read["paid_on"]));
        Assert.Equal(seenAt, Assert.IsType<DateTime>(read["seen_at"]));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)read["seen_at"]!).Kind);
        Assert.False(Assert.IsType<bool>(read["reimbursed"]));
        Assert.Equal(4L, Assert.IsType<long>(read["nights"]));
    }

    [Fact]
    public void An_integer_that_fits_is_widened_rather_than_refused()
    {
        var storage = GivenExpenses();

        var record = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython",
            ["amount_eur"] = 840,
            ["nights"] = (short)4
        });

        Assert.Equal(840m, Assert.IsType<decimal>(record["amount_eur"]));
        Assert.Equal(4L, Assert.IsType<long>(record["nights"]));
    }

    [Fact]
    public void A_timestamp_is_held_in_UTC_whatever_it_arrived_as()
    {
        var storage = GivenExpenses();
        var offset = new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.FromHours(3));

        var record = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython",
            ["amount_eur"] = 840m,
            ["seen_at"] = offset
        });

        Assert.Equal(offset.UtcDateTime, storage.GetById(Expenses, record.Id)!["seen_at"]);
    }

    [Fact]
    public void A_column_written_once_cannot_be_changed_and_the_refusal_says_what_it_held()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython",
            ["amount_eur"] = 840m,
            ["came_from"] = "a spreadsheet"
        });

        var thrown = Assert.Throws<StorageValidationException>(
            () => storage.Update(record.With("came_from", "the chat")));

        var refused = Assert.Single(thrown.Errors.OfType<ReadOnlyColumnChanged>());
        Assert.Equal("came_from", refused.ColumnName);
        Assert.Equal("a spreadsheet", refused.WasValue);
        Assert.Equal("the chat", refused.OfferedValue);

        // Carrying it through unchanged is not a change.
        Assert.True(storage.Update(record.With("city", "Brno")));
        Assert.Equal("a spreadsheet", storage.GetById(Expenses, record.Id)!["came_from"]);
    }

    [Fact]
    public void An_update_carries_the_whole_record_so_a_column_left_out_is_cleared()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        Assert.True(storage.Update(record.Without("city")));

        Assert.False(storage.GetById(Expenses, record.Id)!.Has("city"));
    }

    [Fact]
    public void Updating_a_record_that_is_not_there_says_so_rather_than_creating_one()
    {
        var storage = GivenExpenses();

        Assert.False(storage.Update(new StorageRecord(Ulid.NewUlid(), Expenses, AnExpense())));
        Assert.Empty(storage.GetAll(Expenses));
    }

    // =============================================================================================
    // SC-4, answer 3: a duplicate in a unique column is refused, naming the column and the record
    // that holds the value.
    // =============================================================================================

    [Fact]
    public void A_second_record_with_the_same_value_in_a_unique_column_is_refused()
    {
        var storage = GivenExpenses();
        var holder = storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));

        var thrown = Assert.Throws<StorageValidationException>(
            () => storage.Create(Expenses, AnExpense("DevDays", receipt: "R-0001")));

        var duplicate = Assert.Single(thrown.Errors.OfType<DuplicateValue>());
        Assert.Equal("receipt_no", duplicate.ColumnName);
        Assert.Equal(Expenses, duplicate.CollectionName);
        Assert.Equal("R-0001", duplicate.Value);

        // The record that holds it, which is the answer the user actually wants to see.
        Assert.Equal(holder.Id, duplicate.HeldBy);
        Assert.Equal("EuroPython", storage.GetById(Expenses, duplicate.HeldBy)!["event"]);

        Assert.Single(storage.GetAll(Expenses));
    }

    [Fact]
    public void An_update_to_a_value_another_record_holds_is_refused()
    {
        var storage = GivenExpenses();
        var holder = storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));
        var other = storage.Create(Expenses, AnExpense("DevDays", receipt: "R-0002"));

        var thrown = Assert.Throws<StorageValidationException>(
            () => storage.Update(other.With("receipt_no", "R-0001")));

        Assert.Equal(holder.Id, Assert.Single(thrown.Errors.OfType<DuplicateValue>()).HeldBy);
        Assert.Equal("R-0002", storage.GetById(Expenses, other.Id)!["receipt_no"]);
    }

    [Fact]
    public void A_record_keeping_its_own_value_in_a_unique_column_is_not_a_duplicate_of_itself()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));

        Assert.True(storage.Update(record.With("city", "Brno")));

        var after = storage.GetById(Expenses, record.Id)!;
        Assert.Equal("R-0001", after["receipt_no"]);
        Assert.Equal("Brno", after["city"]);
    }

    /// <summary>
    /// Absence is not a value. A collection that needs the value present says so with
    /// <see cref="ColumnDefinition.Required"/>, which is a separate statement.
    /// </summary>
    [Fact]
    public void Two_records_with_nothing_in_a_unique_column_are_not_duplicates()
    {
        var storage = GivenExpenses();

        storage.Create(Expenses, AnExpense("EuroPython"));
        storage.Create(Expenses, AnExpense("DevDays"));
        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "PyCon",
            ["amount_eur"] = 100m
        });

        Assert.Equal(3, storage.GetAll(Expenses).Count);
    }

    [Fact]
    public void Deleting_the_record_that_holds_a_unique_value_frees_it()
    {
        var storage = GivenExpenses();
        var holder = storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));

        Assert.True(storage.Delete(Expenses, holder.Id));

        var replacement = storage.Create(Expenses, AnExpense("DevDays", receipt: "R-0001"));
        Assert.Equal("R-0001", replacement["receipt_no"]);
    }

    [Fact]
    public void A_unique_column_is_unique_within_its_own_collection_and_no_further()
    {
        var storage = GivenExpenses();
        storage.CreateCollection(new CollectionDefinition("refunds", columns:
        [
            new ColumnDefinition("receipt_no", ColumnType.Text, unique: true)
        ]));

        storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));
        var refund = storage.Create("refunds", new Dictionary<string, object?> { ["receipt_no"] = "R-0001" });

        Assert.Equal("R-0001", refund["receipt_no"]);
    }

    // =============================================================================================
    // SC-4, answer 5: GetAll promises no order.
    // =============================================================================================

    /// <summary>
    /// What can be asserted is that every record comes back, said in a way that is indifferent to
    /// the order. What cannot be asserted is the absence of a promise, so the discipline is in the
    /// suite instead: nothing here reads <c>GetAll(...).First()</c> or indexes the result.
    /// </summary>
    [Fact]
    public void GetAll_returns_every_record_and_says_nothing_about_their_order()
    {
        var storage = GivenExpenses();

        var written = Enumerable.Range(0, 20)
            .Select(i => storage.Create(Expenses, AnExpense($"event {i}")))
            .ToArray();

        var read = storage.GetAll(Expenses);

        Assert.Equal(written.Length, read.Count);
        Assert.Equal(
            written.Select(static record => record.Id).Order(),
            read.Select(static record => record.Id).Order());
        Assert.Equal(written.OrderBy(static r => r.Id), read.OrderBy(static r => r.Id));
    }

    [Fact]
    public void A_caller_who_wants_an_order_sorts_by_identity_and_gets_the_order_they_were_created_in()
    {
        var storage = GivenExpenses();

        var written = Enumerable.Range(0, 20)
            .Select(i => storage.Create(Expenses, AnExpense($"event {i}")))
            .ToArray();

        Assert.Equal(
            written.Select(static record => record["event"]),
            storage.GetAll(Expenses).OrderBy(static record => record.Id).Select(static record => record["event"]));
    }

    [Fact]
    public void An_empty_collection_has_no_records()
    {
        Assert.Empty(GivenExpenses().GetAll(Expenses));
    }

    // =============================================================================================
    // SC-5, the unit of work. The import of five hundred records is 1.4's; what is here is that
    // the thing exists from the first version and that it undoes what it says it undoes.
    // =============================================================================================

    [Fact]
    public void Work_that_finishes_is_kept()
    {
        var storage = GivenExpenses();

        storage.InUnitOfWork(() =>
        {
            storage.Create(Expenses, AnExpense("EuroPython"));
            storage.Create(Expenses, AnExpense("DevDays"));
        });

        Assert.Equal(2, storage.GetAll(Expenses).Count);
    }

    [Fact]
    public void Work_that_throws_half_way_leaves_nothing()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense("before"));

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.Create(Expenses, AnExpense("during one"));
            storage.Create(Expenses, AnExpense("during two"));
            throw new InvalidOperationException("the file ran out half way through");
        }));

        var left = Assert.Single(storage.GetAll(Expenses));
        Assert.Equal("before", left["event"]);
    }

    [Fact]
    public void A_refusal_inside_a_unit_of_work_undoes_the_writes_that_came_before_it()
    {
        var storage = GivenExpenses();

        Assert.Throws<StorageValidationException>(() => storage.InUnitOfWork(() =>
        {
            storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));
            storage.Create(Expenses, AnExpense("DevDays", receipt: "R-0001"));
        }));

        Assert.Empty(storage.GetAll(Expenses));
    }

    [Fact]
    public void A_unit_of_work_inside_a_unit_of_work_joins_the_outer_one()
    {
        var storage = GivenExpenses();

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.InUnitOfWork(() => storage.Create(Expenses, AnExpense("inner")));
            throw new InvalidOperationException("the outer one failed after the inner one finished");
        }));

        // The inner one "finished", but only the outer one could commit, and it did not.
        Assert.Empty(storage.GetAll(Expenses));
    }

    [Fact]
    public void A_structural_change_inside_a_unit_of_work_is_undone_with_the_rest()
    {
        var storage = Storage;

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.CreateCollection(ExpensesDefinition());
            storage.Create(Expenses, AnExpense());
            throw new InvalidOperationException("no");
        }));

        Assert.Null(storage.GetCollectionDefinition(Expenses));
    }

    [Fact]
    public void A_unit_of_work_can_hand_back_what_it_made()
    {
        var storage = GivenExpenses();

        var written = storage.InUnitOfWork(() => new[]
        {
            storage.Create(Expenses, AnExpense("EuroPython")),
            storage.Create(Expenses, AnExpense("DevDays"))
        });

        Assert.Equal(2, written.Length);
        Assert.Equal(
            written.Select(static record => record.Id).Order(),
            storage.GetAll(Expenses).Select(static record => record.Id).Order());
    }

    // =============================================================================================
    // SC-5, the import. The unit of work above says a failure leaves nothing; this is the size the
    // requirement names, because five hundred records is where the difference between one commit
    // and five hundred stops being theoretical.
    // =============================================================================================

    [Fact]
    public void An_import_of_five_hundred_records_goes_in_together()
    {
        var storage = GivenExpenses();

        var written = storage.InUnitOfWork(() => Enumerable.Range(0, 500)
            .Select(i => storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = $"row {i}",
                ["amount_eur"] = 10m + i,
                ["receipt_no"] = $"R-{i:0000}"
            }))
            .ToArray());

        Assert.Equal(500, written.Length);
        Assert.Equal(500, storage.GetAll(Expenses).Count);
        Assert.Equal(500, written.Select(static record => record.Id).Distinct().Count());
    }

    [Fact]
    public void An_import_of_five_hundred_records_that_fails_at_the_end_leaves_nothing()
    {
        var storage = GivenExpenses();

        Assert.Throws<StorageValidationException>(() => storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < 499; i++)
            {
                storage.Create(Expenses, new Dictionary<string, object?>
                {
                    ["event"] = $"row {i}",
                    ["amount_eur"] = 10m + i,
                    ["receipt_no"] = $"R-{i:0000}"
                });
            }

            // The five hundredth row of a real spreadsheet, with the receipt number of the first.
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = "row 499",
                ["amount_eur"] = 509m,
                ["receipt_no"] = "R-0000"
            });
        }));

        Assert.Empty(storage.GetAll(Expenses));
    }

    // =============================================================================================
    // SC-6, structural change. Adding and removing a collection are tested above; these are the
    // four column ones and the converge command.
    // =============================================================================================

    [Fact]
    public void A_column_can_be_added_and_records_that_already_exist_have_no_value_for_it()
    {
        var storage = GivenExpenses();
        var before = storage.Create(Expenses, AnExpense());

        storage.AddColumn(Expenses, new ColumnDefinition("venue", ColumnType.Text, "where it was held"));

        Assert.Equal(
            ColumnType.Text,
            storage.GetCollectionDefinition(Expenses)!.Column("venue")!.Type);

        var after = storage.GetById(Expenses, before.Id)!;
        Assert.False(after.Has("venue"));

        // And the column is now writable, on an old record and a new one alike.
        Assert.True(storage.Update(after.With("venue", "Prague Congress Centre")));
        Assert.Equal("Prague Congress Centre", storage.GetById(Expenses, before.Id)!["venue"]);
    }

    /// <summary>
    /// A default is what a record starts with. A record that already existed did not start with
    /// this column at all, so adding one with a default does not reach back and give it one.
    /// </summary>
    [Fact]
    public void Adding_a_column_with_a_default_does_not_give_it_to_records_that_already_exist()
    {
        var storage = GivenExpenses();
        var before = storage.Create(Expenses, AnExpense());

        storage.AddColumn(Expenses, new ColumnDefinition("country", ColumnType.Text, defaultValue: "Czechia"));

        Assert.False(storage.GetById(Expenses, before.Id)!.Has("country"));
        Assert.Equal("Czechia", storage.Create(Expenses, AnExpense("DevDays"))["country"]);
    }

    [Fact]
    public void A_column_that_is_already_there_cannot_be_added_again()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<ColumnAlreadyExistsException>(
            () => storage.AddColumn(Expenses, new ColumnDefinition("city", ColumnType.Integer)));

        Assert.Equal("city", thrown.ColumnName);
        Assert.Equal(ColumnType.Text, storage.GetCollectionDefinition(Expenses)!.Column("city")!.Type);
    }

    [Fact]
    public void A_column_can_be_renamed_and_keeps_its_values_and_everything_about_it()
    {
        var storage = GivenExpenses();
        var before = storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));

        storage.RenameColumn(Expenses, "receipt_no", "receipt_number");

        var definition = storage.GetCollectionDefinition(Expenses)!;
        Assert.Null(definition.Column("receipt_no"));

        var renamed = definition.Column("receipt_number")!;
        Assert.Equal(ColumnType.Text, renamed.Type);
        Assert.True(renamed.Unique);
        Assert.Equal("the receipt's own number", renamed.Purpose);

        var after = storage.GetById(Expenses, before.Id)!;
        Assert.Equal("R-0001", after["receipt_number"]);
        Assert.False(after.Has("receipt_no"));
    }

    /// <summary>
    /// The display rule names columns. One naming a column that no longer exists is broken, and
    /// the rename is the only moment at which it is known what it should say instead.
    /// </summary>
    [Fact]
    public void Renaming_a_column_rewrites_the_display_rule_that_names_it()
    {
        var storage = GivenExpenses();

        storage.RenameColumn(Expenses, "city", "town");

        Assert.Equal(
            new DisplayRule("{event} in {town}"),
            storage.GetCollectionDefinition(Expenses)!.DisplayRule);
    }

    [Fact]
    public void A_column_cannot_be_renamed_onto_a_name_that_is_taken()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<ColumnAlreadyExistsException>(
            () => storage.RenameColumn(Expenses, "city", "event"));

        Assert.Equal("event", thrown.ColumnName);
        Assert.NotNull(storage.GetCollectionDefinition(Expenses)!.Column("city"));
    }

    [Fact]
    public void A_column_that_is_not_there_cannot_be_renamed_retyped_or_removed()
    {
        var storage = GivenExpenses();

        foreach (var operation in new Action[]
                 {
                     () => storage.RenameColumn(Expenses, "venue", "place"),
                     () => storage.RetypeColumn(Expenses, "venue", ColumnType.Text),
                     () => storage.RemoveColumn(Expenses, "venue")
                 })
        {
            var thrown = Assert.Throws<UnknownColumnException>(operation);
            Assert.Equal("venue", thrown.ColumnName);
            Assert.Equal(Expenses, thrown.CollectionName);
        }
    }

    /// <summary>
    /// SC-6's own acceptance condition. The records on the older side of the change were written
    /// as text and are read as numbers, without having been rewritten.
    /// </summary>
    [Fact]
    public void A_retyped_column_serves_records_written_on_both_sides_of_the_change()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text, required: true),
            new ColumnDefinition("value", ColumnType.Text, "written as text, before anyone noticed")
        ]));

        var before = storage.Create("readings", new Dictionary<string, object?>
        {
            ["label"] = "written before",
            ["value"] = "840"
        });

        storage.RetypeColumn("readings", "value", ColumnType.Integer);

        var after = storage.Create("readings", new Dictionary<string, object?>
        {
            ["label"] = "written after",
            ["value"] = 1250L
        });

        // Both sides, as numbers, from one collection.
        Assert.Equal(840L, Assert.IsType<long>(storage.GetById("readings", before.Id)!["value"]));
        Assert.Equal(1250L, Assert.IsType<long>(storage.GetById("readings", after.Id)!["value"]));

        Assert.Equal(
            [840L, 1250L],
            storage.GetAll("readings").OrderBy(static record => record.Id).Select(static record => record["value"]));
    }

    /// <summary>
    /// The loss D-7 has the application count and ask about, and the reason a retype is not
    /// refused when it cannot convert: a value with no meaning under the new type is in the same
    /// position as a record written before the column existed.
    /// </summary>
    [Fact]
    public void A_value_that_cannot_be_read_as_the_new_type_becomes_nothing()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text, required: true),
            new ColumnDefinition("value", ColumnType.Text)
        ]));

        var number = storage.Create("readings", new Dictionary<string, object?>
        {
            ["label"] = "a number", ["value"] = "840"
        });
        var words = storage.Create("readings", new Dictionary<string, object?>
        {
            ["label"] = "not a number", ["value"] = "about eight hundred"
        });

        storage.RetypeColumn("readings", "value", ColumnType.Integer);

        Assert.Equal(840L, storage.GetById("readings", number.Id)!["value"]);
        Assert.Null(storage.GetById("readings", words.Id)!["value"]);
    }

    [Theory]
    [InlineData(ColumnType.Integer, 840L, ColumnType.Text, "840")]
    [InlineData(ColumnType.Boolean, true, ColumnType.Text, "True")]
    [InlineData(ColumnType.Text, "True", ColumnType.Boolean, true)]
    [InlineData(ColumnType.Text, "yes", ColumnType.Boolean, null)]
    [InlineData(ColumnType.Text, "840", ColumnType.Integer, 840L)]
    [InlineData(ColumnType.Integer, 840L, ColumnType.Decimal, null)]
    public void A_retype_converts_a_value_through_its_invariant_text(
        ColumnType from,
        object written,
        ColumnType to,
        object? expected) =>
        // A decimal cannot be spelled in an attribute, so the one case that needs one passes
        // null and is filled in here rather than being left out of the table.
        AssertRetype(from, written, to, expected ?? (to is ColumnType.Decimal ? 840m : null));

    [Fact]
    public void A_decimal_that_is_not_whole_does_not_survive_being_retyped_to_a_whole_number() =>
        AssertRetype(ColumnType.Decimal, 840.50m, ColumnType.Integer, null);

    [Fact]
    public void A_decimal_retyped_to_text_keeps_its_scale() =>
        AssertRetype(ColumnType.Decimal, 840.50m, ColumnType.Text, "840.50");

    private void AssertRetype(ColumnType from, object written, ColumnType to, object? expected)
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
            [new ColumnDefinition("value", from)]));

        var record = storage.Create("readings", new Dictionary<string, object?> { ["value"] = written });

        storage.RetypeColumn("readings", "value", to);

        Assert.Equal(expected, storage.GetById("readings", record.Id)!["value"]);
    }

    [Fact]
    public void Retyping_a_column_to_the_type_it_already_has_changes_nothing()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());
        var definition = storage.GetCollectionDefinition(Expenses);

        storage.RetypeColumn(Expenses, "amount_eur", ColumnType.Decimal);

        Assert.Equal(definition, storage.GetCollectionDefinition(Expenses));
        Assert.Equal(record, storage.GetById(Expenses, record.Id));
    }

    [Fact]
    public void A_column_can_be_removed_and_takes_its_values_with_it()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        storage.RemoveColumn(Expenses, "nights");

        Assert.Null(storage.GetCollectionDefinition(Expenses)!.Column("nights"));
        Assert.False(storage.GetById(Expenses, record.Id)!.Has("nights"));

        // And writing to it is now a column the collection does not have.
        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Expenses,
            new Dictionary<string, object?>
            {
                ["event"] = "DevDays", ["amount_eur"] = 100m, ["nights"] = 2L
            }));

        Assert.Single(thrown.Errors.OfType<UnknownColumn>());
    }

    [Fact]
    public void A_column_the_display_rule_names_cannot_be_removed_while_it_names_it()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<InvalidDefinitionException>(() => storage.RemoveColumn(Expenses, "city"));
        Assert.Equal("display rule", thrown.Member);
        Assert.NotNull(storage.GetCollectionDefinition(Expenses)!.Column("city"));

        // Saying what the collection should read as instead is the caller's decision, and once
        // they have made it the column goes.
        storage.CreateCollection(new CollectionDefinition("plain", columns:
            [new ColumnDefinition("city", ColumnType.Text)]));
        storage.RemoveColumn("plain", "city");

        Assert.Empty(storage.GetCollectionDefinition("plain")!.Columns);
    }

    /// <summary>
    /// SC-6's other acceptance condition. Converging changes nothing anyone can see, which is
    /// the point: it is the same records, no longer read through anything.
    /// </summary>
    [Fact]
    public void Converging_changes_nothing_that_can_be_seen_and_can_be_done_twice()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text),
            new ColumnDefinition("value", ColumnType.Text)
        ]));

        foreach (var i in Enumerable.Range(0, 20))
        {
            storage.Create("readings", new Dictionary<string, object?>
            {
                ["label"] = $"reading {i}", ["value"] = (100 + i).ToString()
            });
        }

        storage.RetypeColumn("readings", "value", ColumnType.Integer);
        storage.RenameColumn("readings", "value", "amount");

        var definition = storage.GetCollectionDefinition("readings");
        var before = storage.GetAll("readings").OrderBy(static record => record.Id).ToArray();

        storage.Converge("readings");

        Assert.Equal(definition, storage.GetCollectionDefinition("readings"));
        Assert.Equal(before, storage.GetAll("readings").OrderBy(static record => record.Id));

        // Idempotent: the second one has nothing left to do, and says so.
        Assert.Equal(0, storage.Converge("readings"));
        Assert.Equal(before, storage.GetAll("readings").OrderBy(static record => record.Id));
    }

    [Fact]
    public void Converging_a_collection_that_never_changed_does_nothing()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense());

        Assert.Equal(0, storage.Converge(Expenses));
    }

    [Fact]
    public void A_structural_change_to_a_collection_that_is_not_there_says_which_collection()
    {
        foreach (var operation in new Action[]
                 {
                     () => Storage.AddColumn("nothing_like_this", new ColumnDefinition("a", ColumnType.Text)),
                     () => Storage.RenameColumn("nothing_like_this", "a", "b"),
                     () => Storage.RetypeColumn("nothing_like_this", "a", ColumnType.Text),
                     () => Storage.RemoveColumn("nothing_like_this", "a"),
                     () => Storage.Converge("nothing_like_this")
                 })
        {
            Assert.Equal("nothing_like_this", Assert.Throws<UnknownCollectionException>(operation).CollectionName);
        }
    }

    /// <summary>
    /// A structural change is a write like any other, so a unit of work that fails takes it back.
    /// </summary>
    [Fact]
    public void A_column_added_inside_work_that_failed_is_not_there_afterwards()
    {
        var storage = GivenExpenses();

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.AddColumn(Expenses, new ColumnDefinition("venue", ColumnType.Text));
            throw new InvalidOperationException("no");
        }));

        Assert.Null(storage.GetCollectionDefinition(Expenses)!.Column("venue"));
    }

    // =============================================================================================
    // SC-7. One declarative query type, checked against the schema before anything runs, and the
    // access path in the result.
    // =============================================================================================

    [Fact]
    public void A_query_with_no_conditions_returns_everything()
    {
        var storage = GivenExpenses();
        foreach (var i in Enumerable.Range(0, 10))
        {
            storage.Create(Expenses, AnExpense($"event {i}"));
        }

        var result = storage.ExecuteQuery(new StorageQuery(Expenses));

        Assert.Equal(10, result.Records.Count);
        Assert.Equal(Expenses, result.CollectionName);
    }

    [Fact]
    public void A_condition_narrows_the_records_to_the_ones_that_satisfy_it()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense("EuroPython"));
        storage.Create(Expenses, AnExpense("DevDays"));
        storage.Create(Expenses, AnExpense("PyCon"));

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", QueryOperator.Equals, "DevDays")]));

        Assert.Equal("DevDays", Assert.Single(result.Records)["event"]);
    }

    [Fact]
    public void Every_condition_has_to_be_true_of_a_record_not_just_one_of_them()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython", ["city"] = "Prague", ["amount_eur"] = 840m
        });
        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython", ["city"] = "Dublin", ["amount_eur"] = 200m
        });

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
        [
            new QueryCondition("event", QueryOperator.Equals, "EuroPython"),
            new QueryCondition("city", QueryOperator.Equals, "Prague")
        ]));

        Assert.Equal(840m, Assert.Single(result.Records)["amount_eur"]);
    }

    [Fact]
    public void A_range_is_two_conditions_and_reads_the_way_a_person_would_ask_it()
    {
        var storage = GivenExpenses();

        foreach (var day in new[] { 1, 15, 30 })
        {
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = $"day {day}",
                ["amount_eur"] = 10m,
                ["paid_on"] = new DateOnly(2026, 6, day)
            });
        }

        // "what did I spend in the second half of June"
        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
        [
            new QueryCondition("paid_on", QueryOperator.GreaterOrEqual, new DateOnly(2026, 6, 14)),
            new QueryCondition("paid_on", QueryOperator.LessOrEqual, new DateOnly(2026, 6, 30))
        ], orderBy: [new QuerySort("paid_on")]));

        Assert.Equal(
            [new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 30)],
            result.Records.Select(static record => record["paid_on"]));
    }

    [Fact]
    public void Several_values_of_one_column_are_one_condition()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense("EuroPython"));
        storage.Create(Expenses, AnExpense("DevDays"));
        storage.Create(Expenses, AnExpense("PyCon"));

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", QueryOperator.In, "EuroPython", "PyCon")]));

        Assert.Equal(
            ["EuroPython", "PyCon"],
            result.Records.Select(static record => record["event"]).OrderBy(static value => (string)value!));
    }

    /// <summary>
    /// Text is compared with its case folded and its accents dropped - the form the engine's
    /// index keys are in, so that the same query cannot answer differently on two machines with
    /// different locales. For someone searching their own data it is the forgiving behaviour
    /// they would expect, which is why it is the contract's rule and not only the engine's.
    /// </summary>
    [Theory]
    [InlineData(QueryOperator.StartsWith, "Euro", 1)]
    [InlineData(QueryOperator.StartsWith, "euro", 1)]
    [InlineData(QueryOperator.StartsWith, "EURO", 1)]
    [InlineData(QueryOperator.EndsWith, "thon", 1)]
    [InlineData(QueryOperator.EndsWith, "THON", 1)]
    [InlineData(QueryOperator.Contains, "oPy", 1)]
    [InlineData(QueryOperator.Contains, "OPY", 1)]
    [InlineData(QueryOperator.StartsWith, "Python", 0)]
    [InlineData(QueryOperator.Contains, "conference", 0)]
    public void Text_is_matched_with_its_case_folded(QueryOperator @operator, string value, int expected)
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense("EuroPython"));
        storage.Create(Expenses, AnExpense("DevDays"));

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", @operator, value)]));

        Assert.Equal(expected, result.Records.Count);
    }

    [Theory]
    [InlineData("Zurich")]
    [InlineData("zurich")]
    [InlineData("ZURICH")]
    [InlineData("Zürich")]
    public void Two_spellings_of_one_word_are_one_word_to_a_query(string asked)
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "a conference", ["city"] = "Zürich", ["amount_eur"] = 100m
        });

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("city", QueryOperator.Equals, asked)]));

        // Stored as it was written, whatever it was asked for by.
        Assert.Equal("Zürich", Assert.Single(result.Records)["city"]);
    }

    /// <summary>
    /// The same rule where it costs something rather than where it helps: a unique column is
    /// enforced by the index that does the folding, so two spellings are one value there too.
    /// Worth pinning, because it is the one place the rule refuses something rather than finding
    /// something.
    /// </summary>
    [Fact]
    public void A_unique_text_column_treats_two_spellings_as_one_value()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense("EuroPython", receipt: "R-0001"));

        var thrown = Assert.Throws<StorageValidationException>(
            () => storage.Create(Expenses, AnExpense("DevDays", receipt: "r-0001")));

        Assert.Equal("receipt_no", Assert.Single(thrown.Errors.OfType<DuplicateValue>()).ColumnName);
    }

    /// <summary>
    /// The limit the folding rule carries with it, pinned so that it is a known cost rather than
    /// a surprise: a comparison sees the first 128 characters, because that is where the engine's
    /// string key stops. Two longer texts that agree that far are one text to a query - and, less
    /// comfortably, to a unique column.
    /// </summary>
    [Fact]
    public void Two_long_texts_that_agree_for_the_first_128_characters_are_one_text_to_a_query()
    {
        var storage = GivenExpenses();
        var shared = new string('a', 128);

        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = shared + " one", ["amount_eur"] = 1m
        });
        storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = shared + " two", ["amount_eur"] = 2m
        });

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", QueryOperator.Equals, shared + " one")]));

        Assert.Equal(2, result.Records.Count);

        // One character earlier and they are two texts again, which is what says the limit is
        // where it is said to be rather than somewhere vaguer.
        var storageAgain = Track(NewStorage());
        storageAgain.CreateCollection(ExpensesDefinition());
        var shorter = new string('a', 120);

        storageAgain.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = shorter + " one", ["amount_eur"] = 1m
        });
        storageAgain.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = shorter + " two", ["amount_eur"] = 2m
        });

        Assert.Single(storageAgain.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", QueryOperator.Equals, shorter + " one")])).Records);
    }

    [Fact]
    public void Text_is_ordered_with_its_case_folded_too()
    {
        var storage = GivenExpenses();
        foreach (var city in new[] { "Zurich", "aachen", "Brno" })
        {
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = $"in {city}", ["city"] = city, ["amount_eur"] = 1m
            });
        }

        Assert.Equal(
            ["aachen", "Brno", "Zurich"],
            storage.ExecuteQuery(new StorageQuery(Expenses, orderBy: [new QuerySort("city")]))
                .Records.Select(static record => record["city"]));
    }

    /// <summary>
    /// The rule the contract's documentation states, and the one a person asking would expect: a
    /// record that was never given a city is not a record whose city is other than Prague.
    /// </summary>
    [Fact]
    public void A_column_with_no_value_satisfies_nothing_but_IsNothing()
    {
        var storage = GivenExpenses();
        var withCity = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "EuroPython", ["city"] = "Prague", ["amount_eur"] = 840m
        });
        var neverGiven = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "DevDays", ["amount_eur"] = 200m
        });
        var givenNothing = storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["event"] = "PyCon", ["city"] = null, ["amount_eur"] = 100m
        });

        Assert.Equal(
            [neverGiven.Id, givenNothing.Id],
            Ids(storage, [new QueryCondition("city", QueryOperator.IsNothing)]));

        Assert.Equal(
            [withCity.Id],
            Ids(storage, [new QueryCondition("city", QueryOperator.IsSomething)]));

        // Neither of the two without a city is "a record whose city is not Prague".
        Assert.Empty(Ids(storage, [new QueryCondition("city", QueryOperator.NotEquals, "Prague")]));

        Assert.Equal(
            [withCity.Id],
            Ids(storage, [new QueryCondition("city", QueryOperator.Equals, "Prague")]));
    }

    [Fact]
    public void A_query_can_ask_for_records_by_identity()
    {
        var storage = GivenExpenses();
        var first = storage.Create(Expenses, AnExpense("EuroPython"));
        storage.Create(Expenses, AnExpense("DevDays"));
        var third = storage.Create(Expenses, AnExpense("PyCon"));

        var result = storage.ExecuteQuery(new StorageQuery(Expenses, ids: [first.Id, third.Id]));

        Assert.Equal(
            [first.Id, third.Id],
            result.Records.Select(static record => record.Id).Order());

        Assert.Equal(QueryAccessPathKind.IdentityLookup, result.AccessPath.Kind);
    }

    [Fact]
    public void A_query_returns_records_in_the_order_it_asked_for()
    {
        var storage = GivenExpenses();
        foreach (var amount in new[] { 300m, 100m, 200m })
        {
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = $"event {amount}", ["amount_eur"] = amount
            });
        }

        Assert.Equal(
            [100m, 200m, 300m],
            storage.ExecuteQuery(new StorageQuery(Expenses, orderBy: [new QuerySort("amount_eur")]))
                .Records.Select(static record => record["amount_eur"]));

        Assert.Equal(
            [300m, 200m, 100m],
            storage.ExecuteQuery(new StorageQuery(Expenses, orderBy: [new QuerySort("amount_eur", Descending: true)]))
                .Records.Select(static record => record["amount_eur"]));
    }

    [Fact]
    public void A_query_can_skip_and_take()
    {
        var storage = GivenExpenses();
        foreach (var i in Enumerable.Range(0, 10))
        {
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["event"] = $"event {i}", ["amount_eur"] = (decimal)i
            });
        }

        var result = storage.ExecuteQuery(new StorageQuery(
            Expenses, orderBy: [new QuerySort("amount_eur")], skip: 3, take: 4));

        Assert.Equal([3m, 4m, 5m, 6m], result.Records.Select(static record => record["amount_eur"]));

        // What was returned is four; what was looked at is all ten, and the cost says so rather
        // than letting the caller believe paging made the query cheaper.
        Assert.Equal(4, result.Cost.RecordsReturned);
        Assert.Equal(10, result.Cost.RecordsExamined);
    }

    // ---- What the result says about how it was reached --------------------------------------

    /// <summary>
    /// SC-7's acceptance condition. A unique column has an index because it is unique - the
    /// enforcement and the access path are the same structure - so this is the query that should
    /// seek, and on a storage with no indexes it is the query that should honestly say it did not.
    /// </summary>
    [Fact]
    public void A_query_over_an_indexed_column_reports_a_seek()
    {
        var storage = GivenExpenses();
        foreach (var i in Enumerable.Range(0, 50))
        {
            storage.Create(Expenses, AnExpense($"event {i}", receipt: $"R-{i:0000}"));
        }

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("receipt_no", QueryOperator.Equals, "R-0031")]));

        Assert.Equal("event 31", Assert.Single(result.Records)["event"]);

        if (SeeksIndexes)
        {
            Assert.Equal(QueryAccessPathKind.IndexSeek, result.AccessPath.Kind);
            Assert.Equal("receipt_no", result.AccessPath.ColumnName);
            Assert.True(result.AccessPath.IsNarrowed);

            // The point of the seek: one record looked at, not fifty.
            Assert.True(
                result.Cost.RecordsExamined < 50,
                $"a seek examined {result.Cost.RecordsExamined} of 50 records");
        }
        else
        {
            Assert.Equal(QueryAccessPathKind.FullScan, result.AccessPath.Kind);
            Assert.False(result.AccessPath.IsNarrowed);
            Assert.Equal(50, result.Cost.RecordsExamined);
        }
    }

    [Fact]
    public void A_query_over_a_column_with_no_index_says_it_read_everything()
    {
        var storage = GivenExpenses();
        foreach (var i in Enumerable.Range(0, 20))
        {
            storage.Create(Expenses, AnExpense($"event {i}"));
        }

        var result = storage.ExecuteQuery(new StorageQuery(Expenses,
            [new QueryCondition("event", QueryOperator.Equals, "event 7")]));

        Assert.Single(result.Records);
        Assert.Equal(QueryAccessPathKind.FullScan, result.AccessPath.Kind);
        Assert.False(result.AccessPath.IsNarrowed);
        Assert.Equal(20, result.Cost.RecordsExamined);
        Assert.Equal(19, result.Cost.RecordsRejected);
    }

    [Fact]
    public void The_access_path_names_the_collection_and_reads_as_a_sentence()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense());

        var path = storage.ExecuteQuery(new StorageQuery(Expenses)).AccessPath;

        Assert.Equal(Expenses, path.CollectionName);
        Assert.Contains(Expenses, path.Description, StringComparison.Ordinal);
        Assert.Equal(path.Description, path.ToString());
    }

    // ---- What a query that does not fit is told ---------------------------------------------

    [Fact]
    public void A_query_naming_a_column_the_collection_does_not_have_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("venue", QueryOperator.Equals, "Prague")])));

        var unknown = Assert.Single(thrown.Errors.OfType<UnknownColumn>());
        Assert.Equal("venue", unknown.ColumnName);
        Assert.Equal(Expenses, unknown.CollectionName);
    }

    [Fact]
    public void A_query_asking_something_of_a_column_that_cannot_be_asked_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("reimbursed", QueryOperator.GreaterThan, true)])));

        var refused = Assert.Single(thrown.Errors.OfType<OperatorNotSuitable>());
        Assert.Equal("reimbursed", refused.ColumnName);
        Assert.Equal(QueryOperator.GreaterThan, refused.Operator);
        Assert.Equal(ColumnType.Boolean, refused.ColumnType);
    }

    [Fact]
    public void Asking_a_date_column_what_it_starts_with_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("paid_on", QueryOperator.StartsWith, "2026")])));

        Assert.Single(thrown.Errors.OfType<OperatorNotSuitable>());
    }

    [Fact]
    public void A_query_comparing_a_column_against_the_wrong_kind_of_value_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses,
                [new QueryCondition("amount_eur", QueryOperator.Equals, "eight hundred and forty")])));

        var mismatch = Assert.Single(thrown.Errors.OfType<ColumnTypeMismatch>());
        Assert.Equal("amount_eur", mismatch.ColumnName);
        Assert.Equal(ColumnType.Decimal, mismatch.Expected);
    }

    [Theory]
    [InlineData(QueryOperator.Equals, 0)]
    [InlineData(QueryOperator.Equals, 2)]
    [InlineData(QueryOperator.In, 0)]
    [InlineData(QueryOperator.IsNothing, 1)]
    public void A_query_giving_an_operator_the_wrong_number_of_values_is_refused(
        QueryOperator @operator,
        int howMany)
    {
        var storage = GivenExpenses();
        var values = Enumerable.Range(0, howMany).Select(static i => (object?)$"value {i}").ToArray();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("event", @operator, values)])));

        var refused = Assert.Single(thrown.Errors.OfType<OperandCountWrong>());
        Assert.Equal("event", refused.ColumnName);
        Assert.Equal(howMany, refused.Offered);
    }

    /// <summary>
    /// Nothing is not a value to compare against, and the refusal says which question to ask
    /// instead - because a model that wrote this almost certainly meant that one.
    /// </summary>
    [Fact]
    public void A_query_comparing_a_column_against_nothing_is_refused_and_says_what_to_ask()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("city", QueryOperator.Equals, [null])])));

        var mismatch = Assert.Single(thrown.Errors.OfType<ColumnTypeMismatch>());
        Assert.Contains(nameof(QueryOperator.IsNothing), mismatch.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_ordered_by_a_column_the_collection_does_not_have_is_refused()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, orderBy: [new QuerySort("venue")])));

        Assert.Equal("venue", Assert.Single(thrown.Errors.OfType<UnknownSortColumn>()).ColumnName);
    }

    [Fact]
    public void Every_reason_a_query_did_not_fit_is_reported_not_only_the_first()
    {
        var storage = GivenExpenses();

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses,
            [
                new QueryCondition("venue", QueryOperator.Equals, "Prague"),
                new QueryCondition("reimbursed", QueryOperator.LessThan, true),
                new QueryCondition("amount_eur", QueryOperator.Equals, "lots")
            ],
            orderBy: [new QuerySort("nowhere")])));

        Assert.Equal(4, thrown.Errors.Count);
        Assert.Single(thrown.Errors.OfType<UnknownColumn>());
        Assert.Single(thrown.Errors.OfType<OperatorNotSuitable>());
        Assert.Single(thrown.Errors.OfType<ColumnTypeMismatch>());
        Assert.Single(thrown.Errors.OfType<UnknownSortColumn>());
    }

    [Fact]
    public void A_query_that_does_not_fit_reads_nothing()
    {
        var storage = GivenExpenses();
        storage.Create(Expenses, AnExpense());

        Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses, [new QueryCondition("venue", QueryOperator.Equals, "Prague")])));

        // Nothing was disturbed, and the collection still answers.
        Assert.Single(storage.ExecuteQuery(new StorageQuery(Expenses)).Records);
    }

    [Fact]
    public void A_query_against_a_collection_that_is_not_there_says_which_collection()
    {
        Assert.Equal(
            "nothing_like_this",
            Assert.Throws<UnknownCollectionException>(
                () => Storage.ExecuteQuery(new StorageQuery("nothing_like_this"))).CollectionName);
    }

    /// <summary>A query is a read, so a retyped column answers one from both sides of the change.</summary>
    [Fact]
    public void A_query_reads_records_from_both_sides_of_a_retype()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("label", ColumnType.Text),
            new ColumnDefinition("value", ColumnType.Text)
        ]));

        storage.Create("readings", new Dictionary<string, object?> { ["label"] = "before", ["value"] = "840" });
        storage.RetypeColumn("readings", "value", ColumnType.Integer);
        storage.Create("readings", new Dictionary<string, object?> { ["label"] = "after", ["value"] = 1250L });

        var result = storage.ExecuteQuery(new StorageQuery("readings",
            [new QueryCondition("value", QueryOperator.GreaterOrEqual, 840L)],
            orderBy: [new QuerySort("value")]));

        Assert.Equal([840L, 1250L], result.Records.Select(static record => record["value"]));
    }

    private Ulid[] Ids(IStorage storage, IReadOnlyList<QueryCondition> where) =>
        [.. storage.ExecuteQuery(new StorageQuery(Expenses, where)).Records.Select(static r => r.Id).Order()];
}
