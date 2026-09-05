using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// The part of <see cref="IStorage"/> that the Phase 4 walking-skeleton adapter implements,
/// run against every backend. Extracted from MemoryStorageTests, which could only ever
/// describe one implementation's behaviour as though it were the contract.
///
/// Where the two backends genuinely differ, the difference is a named property a subclass
/// answers rather than a test that quietly asserts one backend's habits. Those properties
/// are the output of this phase: each one is a decision still to be made, and section 2.2 of
/// the requirements records them.
/// </summary>
public abstract class StorageContractTests : IDisposable
{
    private readonly List<IStorage> _opened = [];

    protected IStorage NewStorage()
    {
        var storage = CreateStorage();
        _opened.Add(storage);
        return storage;
    }

    protected abstract IStorage CreateStorage();

    /// <summary>Whether Create and Update check fields against the schema.</summary>
    protected abstract bool ValidatesRecordFields { get; }

    /// <summary>Whether a missing field is filled from the column's default value.</summary>
    protected abstract bool AppliesColumnDefaults { get; }

    /// <summary>Whether CollectionDefinition.Metadata survives a round trip.</summary>
    protected abstract bool KeepsCollectionMetadata { get; }

    /// <summary>Whether "Customer" and "customer" name the same collection.</summary>
    protected abstract bool CollectionNamesIgnoreCase { get; }

    /// <summary>What creating an existing collection again throws.</summary>
    protected abstract Type DuplicateCollectionExceptionType { get; }

    /// <summary>Whether every ColumnType can hold a value of its own type.</summary>
    protected abstract bool StoresEveryColumnType { get; }

    /// <summary>Whether GetAll stays in insertion order once a record has been deleted.</summary>
    protected abstract bool GetAllOrderSurvivesADelete { get; }

    public virtual void Dispose()
    {
        foreach (var storage in _opened)
        {
            (storage as IDisposable)?.Dispose();
        }
    }

    // ---- collection definitions ----

    [Fact]
    public void ACollectionDefinitionIsReadBackAsItWasCreated()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        var collection = storage.GetCollectionDefinition("Customer");

        Assert.NotNull(collection);
        Assert.Equal("Customer", collection!.Name);
        Assert.Equal("Stores customer records", collection.Description);
        Assert.Equal(
            ["Id", "FirstName", "LastName", "Email"],
            collection.Columns.Select(column => column.Name).ToArray());
        Assert.Contains(collection.Columns, column => column is { Name: "Email", Description: "Primary email" });
        Assert.Contains(collection.Columns, column => column is { Name: "Email", Unique: true });
        Assert.Contains(collection.Columns, column => column is { Name: "Id", ReadOnly: true });
    }

    [Fact]
    public void CollectionMetadataSurvivesOrIsDropped()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        var collection = storage.GetCollectionDefinition("Customer");

        Assert.NotNull(collection);
        if (KeepsCollectionMetadata)
        {
            Assert.Equal("crm", collection!.Metadata["source"]);
        }
        else
        {
            Assert.Empty(collection!.Metadata);
        }
    }

    [Fact]
    public void GetCollectionDefinitionsListsExactlyWhatWasCreated()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderCollection());

        var names = storage.GetCollectionDefinitions().Select(collection => collection.Name).Order().ToArray();

        Assert.Equal(["Customer", "Order"], names);
    }

    [Fact]
    public void AnUnknownCollectionDefinitionIsNullRatherThanAnError()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        Assert.Null(storage.GetCollectionDefinition("NoSuchCollection"));
    }

    [Fact]
    public void CreatingACollectionTwiceIsRefused()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        var exception = Record.Exception(() => storage.CreateCollection(CustomerCollection()));

        Assert.NotNull(exception);
        Assert.IsType(DuplicateCollectionExceptionType, exception);
    }

    [Fact]
    public void CollectionNamesDifferingOnlyInCase()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        if (CollectionNamesIgnoreCase)
        {
            Assert.NotNull(storage.GetCollectionDefinition("customer"));
            Assert.NotNull(Record.Exception(() => storage.CreateCollection(LowerCaseCustomerCollection())));
            return;
        }

        // Two collections, not one: the lookup is ordinal, so nothing stops the pair existing.
        Assert.Null(storage.GetCollectionDefinition("customer"));
        storage.CreateCollection(LowerCaseCustomerCollection());
        Assert.Equal(2, storage.GetCollectionDefinitions().Count);
    }

    // ---- records ----

    [Fact]
    public void ARecordIsCreatedAndReadBackById()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var customerId = Guid.NewGuid();

        var created = storage.Create("Customer", CustomerFields(customerId, "Alice", "Brown", "alice@example.com"));
        var read = storage.GetById("Customer", created.Id);

        Assert.NotEqual(Ulid.Empty, created.Id);
        Assert.NotNull(read);
        Assert.Equal(created.Id, read!.Id);
        Assert.Equal("Customer", read.CollectionName);
        Assert.Equal(customerId, read.Fields["Id"]);
        Assert.Equal("Alice", read.Fields["FirstName"]);
        Assert.Equal("alice@example.com", read.Fields["Email"]);
    }

    [Fact]
    public void GetByIdOfAnIdThatWasNeverStoredIsNull()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        Assert.Null(storage.GetById("Customer", Ulid.NewUlid()));
    }

    [Fact]
    public void AddressingAnUnknownCollectionIsAnInvalidOperation()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        Assert.Throws<InvalidOperationException>(() => storage.GetById("NoSuchCollection", Ulid.NewUlid()));
        Assert.Throws<InvalidOperationException>(() => storage.GetAll("NoSuchCollection"));
        Assert.Throws<InvalidOperationException>(
            () => storage.Create("NoSuchCollection", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "a@b.co")));
    }

    [Fact]
    public void GetAllReturnsEveryLiveRecordAndNothingElse()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var first = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));
        var second = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Bohdan", "Kravets", "bohdan@example.com"));

        var all = storage.GetAll("Customer");

        Assert.Equal(2, all.Count);
        Assert.Contains(all, record => record.Id == first.Id);
        Assert.Contains(all, record => record.Id == second.Id);
        Assert.All(all, record => Assert.Equal("Customer", record.CollectionName));
    }

    [Fact]
    public void GetAllOnAnEmptyCollectionIsEmptyRatherThanNull()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        Assert.Empty(storage.GetAll("Customer"));
    }

    [Fact]
    public void GetAllIsInInsertionOrderWhileNothingHasBeenDeleted()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var expected = Enumerable.Range(0, 5)
            .Select(index => storage.Create(
                "Customer", CustomerFields(Guid.NewGuid(), $"Name{index}", "Brown", $"person{index}@example.com")).Id)
            .ToArray();

        Assert.Equal(expected, storage.GetAll("Customer").Select(record => record.Id).ToArray());
    }

    [Fact]
    public void GetAllOrderAfterARecordHasBeenDeletedAndAnotherInserted()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var first = Insert(storage, "First");
        var second = Insert(storage, "Second");
        var third = Insert(storage, "Third");
        storage.Delete("Customer", second);
        var fourth = Insert(storage, "Fourth");

        var order = storage.GetAll("Customer").Select(record => record.Id).ToArray();

        Ulid[] survivors = [first, third, fourth];
        Assert.Equal(survivors.Order().ToArray(), order.Order().ToArray());
        if (GetAllOrderSurvivesADelete)
        {
            Assert.Equal(survivors, order);
        }
        else
        {
            //The storage reuses the space the deleted record left, so the newest record
            //turns up where the deleted one was. Nothing in IStorage promises otherwise —
            //which is the point: no caller may rely on this.
            Assert.NotEqual(survivors, order);
        }
    }

    [Fact]
    public void UpdateReplacesTheStoredFields()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var customerId = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerFields(customerId, "Alice", "Brown", "alice@example.com"));

        var updated = storage.Update(new StorageRecord(
            created.Id, "Customer", CustomerFields(customerId, "Alicia", "Cooper", "alicia@example.com")));
        var read = storage.GetById("Customer", created.Id);

        Assert.True(updated);
        Assert.NotNull(read);
        Assert.Equal("Alicia", read!.Fields["FirstName"]);
        Assert.Equal("alicia@example.com", read.Fields["Email"]);
        Assert.Single(storage.GetAll("Customer"));
    }

    [Fact]
    public void UpdatingARecordThatIsNoLongerThereIsFalseRatherThanAnError()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var customerId = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerFields(customerId, "Alice", "Brown", "alice@example.com"));
        storage.Delete("Customer", created.Id);

        var updated = storage.Update(new StorageRecord(
            created.Id, "Customer", CustomerFields(customerId, "Alicia", "Cooper", "alicia@example.com")));

        Assert.False(updated);
        Assert.Empty(storage.GetAll("Customer"));
    }

    [Fact]
    public void DeleteRemovesTheRecordAndSaysSoOnlyOnce()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var created = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));

        Assert.True(storage.Delete("Customer", created.Id));
        Assert.False(storage.Delete("Customer", created.Id));
        Assert.Null(storage.GetById("Customer", created.Id));
        Assert.Empty(storage.GetAll("Customer"));
    }

    [Fact]
    public void DeletingOneRecordLeavesTheOthers()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var first = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));
        var second = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Bohdan", "Kravets", "bohdan@example.com"));

        storage.Delete("Customer", first.Id);

        var remaining = Assert.Single(storage.GetAll("Customer"));
        Assert.Equal(second.Id, remaining.Id);
    }

    [Fact]
    public void ANullFieldValueRoundTripsAsNull()
    {
        var storage = NewStorage();
        storage.CreateCollection(NullableCollection());

        var created = storage.Create("Note", new Dictionary<string, object?> { ["Text"] = null });
        var read = storage.GetById("Note", created.Id);

        Assert.NotNull(read);
        Assert.True(read!.Fields.ContainsKey("Text"));
        Assert.Null(read.Fields["Text"]);
    }

    [Fact]
    public void EveryColumnTypeHoldsAValueOfItsOwnType()
    {
        var storage = NewStorage();
        storage.CreateCollection(EveryTypeCollection());
        var guid = Guid.NewGuid();
        var moment = new DateTime(2026, 9, 5, 14, 30, 15, DateTimeKind.Utc);
        var fields = new Dictionary<string, object?>
        {
            ["Text"] = "Проєкт",
            ["Flag"] = true,
            ["Small"] = 42,
            ["Large"] = 9_000_000_000L,
            ["Money"] = 1234.56m,
            ["Moment"] = moment,
            ["Reference"] = guid
        };

        if (!StoresEveryColumnType)
        {
            Assert.NotNull(Record.Exception(() => storage.Create("Everything", fields)));
            return;
        }

        var created = storage.Create("Everything", fields);
        var read = storage.GetById("Everything", created.Id);

        Assert.NotNull(read);
        Assert.Equal("Проєкт", read!.Fields["Text"]);
        Assert.Equal(true, read.Fields["Flag"]);
        Assert.Equal(42, read.Fields["Small"]);
        Assert.Equal(9_000_000_000L, read.Fields["Large"]);
        Assert.Equal(1234.56m, read.Fields["Money"]);
        Assert.Equal(moment, read.Fields["Moment"]);
        Assert.Equal(guid, read.Fields["Reference"]);
    }

    // ---- validation ----

    [Fact]
    public void AMissingFieldIsFilledFromTheColumnDefaultOrLeftOut()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());

        var created = storage.Create("Customer", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["FirstName"] = "Alice",
            ["LastName"] = "Brown"
        });
        var read = storage.GetById("Customer", created.Id);

        Assert.NotNull(read);
        if (AppliesColumnDefaults)
        {
            Assert.Equal("unknown@example.com", read!.Fields["Email"]);
        }
        else
        {
            Assert.False(read!.Fields.ContainsKey("Email"));
        }
    }

    [Fact]
    public void AFieldThatIsNotAColumnIsRejectedOrStored()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var fields = CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com");
        fields["NotAColumn"] = "x";

        if (ValidatesRecordFields)
        {
            var exception = Assert.Throws<StorageValidationException>(() => storage.Create("Customer", fields));
            Assert.Contains(exception.Errors, error => error is { Code: "UnknownColumn", ColumnName: "NotAColumn" });
            return;
        }

        var created = storage.Create("Customer", fields);
        Assert.Equal("x", storage.GetById("Customer", created.Id)!.Fields["NotAColumn"]);
    }

    [Fact]
    public void AValueOfTheWrongTypeIsRejectedOnWriteOrPoisonsTheRecord()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var fields = CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com");
        fields["Id"] = "not-a-guid";

        if (ValidatesRecordFields)
        {
            var exception = Assert.Throws<StorageValidationException>(() => storage.Create("Customer", fields));
            Assert.Contains(exception.Errors, error => error is { Code: "InvalidType", ColumnName: "Id" });
            return;
        }

        //Nothing checks the value on the way in, and the column type is what decodes it on
        //the way out. So the write is accepted and the record can never be read again — and
        //because a scan decodes every record, it takes the whole collection with it.
        var created = storage.Create("Customer", fields);
        Assert.Throws<FormatException>(() => storage.GetById("Customer", created.Id));
        Assert.Throws<FormatException>(() => storage.GetAll("Customer"));
    }

    [Fact]
    public void ADuplicateValueInAUniqueColumnIsRejectedOrStored()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));
        var duplicate = CustomerFields(Guid.NewGuid(), "Alicia", "Cooper", "alice@example.com");

        if (ValidatesRecordFields)
        {
            var exception = Assert.Throws<StorageValidationException>(() => storage.Create("Customer", duplicate));
            Assert.Contains(exception.Errors, error => error is { Code: "UniqueConstraint", ColumnName: "Email" });
            return;
        }

        storage.Create("Customer", duplicate);
        Assert.Equal(2, storage.GetAll("Customer").Count);
    }

    [Fact]
    public void ChangingAReadOnlyColumnIsRejectedOrStored()
    {
        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var created = storage.Create("Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));
        var changed = new StorageRecord(
            created.Id, "Customer", CustomerFields(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));

        if (ValidatesRecordFields)
        {
            var exception = Assert.Throws<StorageValidationException>(() => storage.Update(changed));
            Assert.Contains(exception.Errors, error => error is { Code: "ReadOnlyColumn", ColumnName: "Id" });
            return;
        }

        Assert.True(storage.Update(changed));
    }

    [Fact]
    public void AFailedUpdateLeavesTheStoredRecordAsItWas()
    {
        if (!ValidatesRecordFields)
        {
            return;
        }

        var storage = NewStorage();
        storage.CreateCollection(CustomerCollection());
        var customerId = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerFields(customerId, "Alice", "Brown", "alice@example.com"));

        Assert.Throws<StorageValidationException>(() => storage.Update(new StorageRecord(
            created.Id, "Customer", new Dictionary<string, object?>
            {
                ["Id"] = customerId,
                ["FirstName"] = "Alice",
                ["LastName"] = "Brown",
                ["Email"] = 123
            })));

        Assert.Equal("alice@example.com", storage.GetById("Customer", created.Id)!.Fields["Email"]);
    }

    // ---- fixtures ----

    private static CollectionDefinition CustomerCollection() => new(
        "Customer",
        "Stores customer records",
        [
            new ColumnDefinition("Id", ColumnType.Guid, "Customer id", readOnly: true),
            new ColumnDefinition("FirstName", ColumnType.String, "Given name"),
            new ColumnDefinition("LastName", ColumnType.String, "Family name"),
            new ColumnDefinition("Email", ColumnType.String, "Primary email", unique: true,
                defaultValue: "unknown@example.com")
        ],
        new Dictionary<string, string?> { ["source"] = "crm" });

    private static CollectionDefinition LowerCaseCustomerCollection() => new(
        "customer",
        columns: [new ColumnDefinition("Id", ColumnType.Guid)]);

    private static CollectionDefinition OrderCollection() => new(
        "Order",
        columns: [new ColumnDefinition("Id", ColumnType.Guid), new ColumnDefinition("Number", ColumnType.String)]);

    private static CollectionDefinition NullableCollection() => new(
        "Note",
        columns: [new ColumnDefinition("Text", ColumnType.String)]);

    private static CollectionDefinition EveryTypeCollection() => new(
        "Everything",
        columns: [
            new ColumnDefinition("Text", ColumnType.String),
            new ColumnDefinition("Flag", ColumnType.Boolean),
            new ColumnDefinition("Small", ColumnType.Int32),
            new ColumnDefinition("Large", ColumnType.Int64),
            new ColumnDefinition("Money", ColumnType.Decimal),
            new ColumnDefinition("Moment", ColumnType.DateTime),
            new ColumnDefinition("Reference", ColumnType.Guid)
        ]);

    private static Ulid Insert(IStorage storage, string firstName)
    {
        return storage.Create(
            "Customer",
            CustomerFields(Guid.NewGuid(), firstName, "Brown", $"{firstName.ToLowerInvariant()}@example.com")).Id;
    }

    private static Dictionary<string, object?> CustomerFields(
        Guid id, string firstName, string lastName, string email) => new()
    {
        ["Id"] = id,
        ["FirstName"] = firstName,
        ["LastName"] = lastName,
        ["Email"] = email
    };
}
