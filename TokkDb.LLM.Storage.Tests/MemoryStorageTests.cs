using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

public sealed class MemoryStorageTests
{
    [Fact]
    public void CreateCollectionStoresMetadataAndColumnDescriptions()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());

        var collection = storage.GetCollectionDefinition("Customer");

        Assert.NotNull(collection);
        Assert.Equal("Stores customer records", collection.Description);
        Assert.Equal("crm", collection.Metadata["source"]);
        Assert.Contains(collection.Columns, c => c.Name == "Email" && c.Description == "Primary email");
    }

    [Fact]
    public void GetCollectionDefinitionsReturnsAllCollections()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderCollection());

        var collections = storage.GetCollectionDefinitions();

        Assert.Equal(2, collections.Count);
        Assert.Contains(collections, c => c.Name == "Customer");
        Assert.Contains(collections, c => c.Name == "Order");
    }

    [Fact]
    public void DeleteCollectionRemovesCollectionAndRecords()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var created = storage.Create("Customer", CustomerRecord(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"));

        var deleted = storage.DeleteCollection("Customer");

        Assert.True(deleted);
        Assert.Null(storage.GetCollectionDefinition("Customer"));
        Assert.Throws<InvalidOperationException>(() => storage.GetById("Customer", created.Id));
    }

    [Fact]
    public void AddColumnAndUpdateColumnAndRemoveColumnWork()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.AddColumn("Customer", new ColumnDefinition("Phone", ColumnType.String, "Contact number"));
        var created = storage.Create("Customer", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["FirstName"] = "Alice",
            ["LastName"] = "Brown",
            ["Email"] = "alice@example.com",
            ["Phone"] = "123"
        });

        var updated = storage.UpdateColumn("Customer", "Phone", new ColumnDefinition("MobilePhone", ColumnType.String, "Mobile number"));
        var removed = storage.RemoveColumn("Customer", "MobilePhone");
        var record = storage.GetById("Customer", created.Id);
        var collection = storage.GetCollectionDefinition("Customer");

        Assert.True(updated);
        Assert.True(removed);
        Assert.NotNull(record);
        Assert.NotNull(collection);
        Assert.DoesNotContain(collection.Columns, c => c.Name == "Phone");
        Assert.DoesNotContain(collection.Columns, c => c.Name == "MobilePhone");
        Assert.DoesNotContain(record.Fields, f => f.Key == "MobilePhone");
    }

    [Fact]
    public void SchemaVersionIncrementsOnSchemaChanges()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var created = storage.GetCollectionDefinition("Customer");
        Assert.NotNull(created);
        Assert.Equal("1", created.Metadata["schemaVersion"]);

        storage.AddColumn("Customer", new ColumnDefinition("Phone", ColumnType.String));
        var afterAdd = storage.GetCollectionDefinition("Customer");
        Assert.NotNull(afterAdd);
        Assert.Equal("2", afterAdd.Metadata["schemaVersion"]);

        storage.UpdateColumn("Customer", "Phone", new ColumnDefinition("MobilePhone", ColumnType.String));
        var afterModify = storage.GetCollectionDefinition("Customer");
        Assert.NotNull(afterModify);
        Assert.Equal("3", afterModify.Metadata["schemaVersion"]);

        storage.RemoveColumn("Customer", "MobilePhone");
        var afterDelete = storage.GetCollectionDefinition("Customer");
        Assert.NotNull(afterDelete);
        Assert.Equal("4", afterDelete.Metadata["schemaVersion"]);
    }

    [Fact]
    public void SchemaVersionIncrementsOnRelationChanges()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderWithCustomerIdCollection());

        storage.AddRelation(new RelationDefinition("CustomerOrders", RelationType.ManyToOne, "Order", "CustomerId", "Customer", "Id"));

        var customerAfterCreate = storage.GetCollectionDefinition("Customer");
        var orderAfterCreate = storage.GetCollectionDefinition("Order");
        Assert.NotNull(customerAfterCreate);
        Assert.NotNull(orderAfterCreate);
        Assert.Equal("2", customerAfterCreate.Metadata["schemaVersion"]);
        Assert.Equal("2", orderAfterCreate.Metadata["schemaVersion"]);

        storage.RemoveRelation("CustomerOrders");
        var customerAfterDelete = storage.GetCollectionDefinition("Customer");
        var orderAfterDelete = storage.GetCollectionDefinition("Order");
        Assert.NotNull(customerAfterDelete);
        Assert.NotNull(orderAfterDelete);
        Assert.Equal("3", customerAfterDelete.Metadata["schemaVersion"]);
        Assert.Equal("3", orderAfterDelete.Metadata["schemaVersion"]);
    }

    [Fact]
    public void CollectionValidationRejectsBadNamesTypesAndDuplicates()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());

        Assert.Throws<ArgumentException>(() => new ColumnDefinition("1Invalid", ColumnType.String));
        Assert.Throws<InvalidOperationException>(() => storage.AddColumn("Customer", new ColumnDefinition("Email", ColumnType.String)));
        Assert.Throws<InvalidOperationException>(() => storage.CreateCollection(CustomerCollection()));
    }

    [Fact]
    public void CreateAndGetByIdAssociateRecordsWithCollection()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var customerId = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerRecord(customerId, "Alice", "Brown", "alice@example.com"));

        var record = storage.GetById("Customer", created.Id);

        Assert.NotNull(record);
        Assert.Equal("Customer", record.CollectionName);
        Assert.Equal(customerId, record.Fields["Id"]);
        Assert.Equal("Alice", record.Fields["FirstName"]);
    }

    [Fact]
    public void CreateRejectsUnknownColumnsAndTypeMismatches()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());

        var unknownColumnException = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Customer", CustomerRecord(Guid.NewGuid(), "Alice", "Brown", "alice@example.com", "x")));
        Assert.Contains(unknownColumnException.Errors, e => e.Code == "UnknownColumn" && e.ColumnName == "Unknown");

        var typeMismatchException = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Customer", new Dictionary<string, object?>
            {
                ["Id"] = "not-a-guid",
                ["FirstName"] = "Alice",
                ["LastName"] = "Brown",
                ["Email"] = "alice@example.com"
            }));
        Assert.Contains(typeMismatchException.Errors, e => e.Code == "InvalidType" && e.ColumnName == "Id");
    }

    [Fact]
    public void CreateAppliesDefaultValueWhenMissing()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());

        var created = storage.Create("Customer", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["FirstName"] = "Alice",
            ["LastName"] = "Brown"
        });

        Assert.Equal("unknown@example.com", created.Fields["Email"]);
    }

    [Fact]
    public void CreateReturnsStructuredErrorsForInvalidType()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Customer", new Dictionary<string, object?>
            {
                ["Id"] = "bad-guid",
                ["FirstName"] = "Alice",
                ["Email"] = null
            }));

        Assert.Contains(exception.Errors, e => e is { Code: "InvalidType", ColumnName: "Id" });
    }

    [Fact]
    public void CreateRejectsDuplicatePrimaryKeyAndUniqueValues()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var id = Guid.NewGuid();
        storage.Create("Customer", CustomerRecord(id, "Alice", "Brown", "alice@example.com"));

        var primaryKeyException = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Customer", CustomerRecord(id, "Alicia", "Cooper", "alicia@example.com")));
        Assert.Contains(primaryKeyException.Errors, e => e.Code == "DuplicatePrimaryKey");

        var uniqueException = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Customer", CustomerRecord(Guid.NewGuid(), "Alice", "Brown", "alice@example.com")));
        Assert.Contains(uniqueException.Errors, e => e.Code == "UniqueConstraint" && e.ColumnName == "Email");
    }

    [Fact]
    public void UpdateRejectsReadOnlyColumnChanges()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var id = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerRecord(id, "Alice", "Brown", "alice@example.com"));

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Update(new StorageRecord(created.Id, "Customer", CustomerRecord(Guid.NewGuid(), "Alice", "Brown", "alice@example.com"))));

        Assert.Contains(exception.Errors, e => e.Code == "ReadOnlyColumn" && e.ColumnName == "Id");
    }

    [Fact]
    public void UpdateRejectsInvalidDataAndPreservesStoredRecord()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        var id = Guid.NewGuid();
        var created = storage.Create("Customer", CustomerRecord(id, "Alice", "Brown", "alice@example.com"));

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Update(new StorageRecord(created.Id, "Customer", new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["FirstName"] = "Alice",
                ["LastName"] = "Brown",
                ["Email"] = 123
            })));
        var stored = storage.GetById("Customer", created.Id);

        Assert.Contains(exception.Errors, e => e.Code == "InvalidType" && e.ColumnName == "Email");
        Assert.NotNull(stored);
        Assert.Equal("alice@example.com", stored.Fields["Email"]);
    }

    [Fact]
    public void IntegerColumnRejectsStringValue()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition(
            "Person",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Age", ColumnType.Int32)
            }));

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Person", new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid(),
                ["Age"] = "twenty"
            }));

        Assert.Contains(exception.Errors, e => e.Code == "InvalidType" && e.ColumnName == "Age");
    }

    [Fact]
    public void RelationsSupportAllCardinalities()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderWithCustomerIdCollection());
        storage.CreateCollection(ProfileCollection());
        storage.CreateCollection(TagCollection());
        storage.CreateCollection(TagLinkCollection());

        var customerId = Guid.NewGuid();
        storage.Create("Customer", CustomerRecord(customerId, "Alice", "Brown", "alice@example.com"));
        storage.Create("Order", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["CustomerId"] = customerId, ["Number"] = "ORD-1" });
        storage.Create("Profile", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["OwnerId"] = customerId });
        storage.Create("Tag", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["Name"] = "vip" });
        storage.Create("TagLink", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["TagName"] = "vip" });

        storage.AddRelation(new RelationDefinition("CustomerOrders", RelationType.ManyToOne, "Order", "CustomerId", "Customer", "Id"));
        storage.AddRelation(new RelationDefinition("CustomerProfiles", RelationType.OneToMany, "Customer", "Id", "Profile", "OwnerId"));
        storage.AddRelation(new RelationDefinition("CustomerPrimaryOrder", RelationType.OneToOne, "Profile", "OwnerId", "Customer", "Id"));
        storage.AddRelation(new RelationDefinition("TagLinks", RelationType.ManyToMany, "Tag", "Name", "TagLink", "TagName"));

        var relations = storage.GetRelations();

        Assert.Equal(4, relations.Count);
    }

    [Fact]
    public void AddRelationValidatesCollectionsColumnsAndTypes()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderWithCustomerIdCollection());

        Assert.Throws<InvalidOperationException>(() =>
            storage.AddRelation(new RelationDefinition("BadCollection", RelationType.ManyToOne, "Unknown", "CustomerId", "Customer", "Id")));

        Assert.Throws<InvalidOperationException>(() =>
            storage.AddRelation(new RelationDefinition("BadSourceColumn", RelationType.ManyToOne, "Order", "Unknown", "Customer", "Id")));

        Assert.Throws<InvalidOperationException>(() =>
            storage.AddRelation(new RelationDefinition("BadType", RelationType.ManyToOne, "Order", "Number", "Customer", "Id")));
    }

    [Fact]
    public void CreateAndDeletePreventRelationViolations()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(CustomerCollection());
        storage.CreateCollection(OrderWithCustomerIdCollection());

        var customerId = Guid.NewGuid();
        var customer = storage.Create("Customer", CustomerRecord(customerId, "Alice", "Brown", "alice@example.com"));
        storage.AddRelation(new RelationDefinition("CustomerOrders", RelationType.ManyToOne, "Order", "CustomerId", "Customer", "Id"));

        var createException = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Order", new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid(),
                ["CustomerId"] = Guid.NewGuid(),
                ["Number"] = "ORD-404"
            }));
        Assert.Contains(createException.Errors, e => e.Code == "MissingReferencedRecord");

        var validOrder = storage.Create("Order", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["CustomerId"] = customerId,
            ["Number"] = "ORD-1"
        });
        Assert.NotNull(validOrder);

        var deleteException = Assert.Throws<StorageValidationException>(() => storage.Delete("Customer", customer.Id));
        Assert.Contains(deleteException.Errors, e => e.Code == "MissingReferencedRecord");
    }

    [Fact]
    public void SemanticTypeIsValidatedWhenUsedByColumn()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "email",
            "Email",
            "Electronic mail address",
            ColumnType.String,
            Aliases: new[] { "E-mail" },
            ValidationPattern: @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition(
            "Contact",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Email", ColumnType.String, semanticTypeName: "email")
            }));

        storage.Create("Contact", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Email"] = "valid@example.com"
        });

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Contact", new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid(),
                ["Email"] = "not-an-email"
            }));
        Assert.Contains(exception.Errors, e => e.Code == "InvalidSemanticValue" && e.ColumnName == "Email");
    }

    [Fact]
    public void SemanticTypeNormalizationRunsBeforeValidationAndStoresNormalizedValue()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "phone",
            "Phone",
            "International phone number",
            ColumnType.String,
            ValidationPattern: @"^\+\d{12}$",
            NormalizationRules: new[] { "Trim", "RemoveWhitespace", "RemoveCharacters:()-" }));
        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition(
            "Contact",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Phone", ColumnType.String, semanticTypeName: "phone")
            }));

        var created = storage.Create("Contact", new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Phone"] = "+380 (50) 123-45-67"
        });

        Assert.Equal("+380501234567", created.Fields["Phone"]);
    }

    [Fact]
    public void ColumnValidationRulesAreAppliedInAdditionToSemanticRules()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "email",
            "Email",
            "Electronic mail address",
            ColumnType.String,
            ValidationPattern: @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
            NormalizationRules: new[] { "Trim", "ToLowerInvariant" }));
        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition(
            "Contact",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition(
                    "Email",
                    ColumnType.String,
                    semanticTypeName: "email",
                    validationPattern: @"^[a-z0-9._%+\-]+@example\.com$")
            }));

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Contact", new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid(),
                ["Email"] = "User@other.org"
            }));

        Assert.Contains(exception.Errors, e => e.Code == "InvalidColumnValidation" && e.ColumnName == "Email");
    }

    [Fact]
    public void InvalidNormalizedValueIsRejected()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "phone",
            "Phone",
            "International phone number",
            ColumnType.String,
            ValidationPattern: @"^\+\d{12}$",
            NormalizationRules: new[] { "Trim", "RemoveWhitespace", "RemoveCharacters:()-" }));
        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition(
            "Contact",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Phone", ColumnType.String, semanticTypeName: "phone")
            }));

        var exception = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Contact", new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid(),
                ["Phone"] = "0501234567"
            }));

        Assert.Contains(exception.Errors, e => e.Code == "InvalidSemanticValue" && e.ColumnName == "Phone");
    }

    [Fact]
    public void UnknownSemanticTypeIsRejected()
    {
        var registry = new SemanticTypeRegistry();
        var storage = new MemoryStorage(registry);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            storage.CreateCollection(new CollectionDefinition(
                "Contact",
                columns: new[]
                {
                    new ColumnDefinition("Id", ColumnType.Guid),
                    new ColumnDefinition("Email", ColumnType.String, semanticTypeName: "unknown_semantic")
                })));

        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CollectionDefinition CustomerCollection()
    {
        return new CollectionDefinition(
            "Customer",
            "Stores customer records",
            new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid, "Customer id", readOnly: true),
                new ColumnDefinition("FirstName", ColumnType.String, "Given name"),
                new ColumnDefinition("LastName", ColumnType.String, "Family name"),
                new ColumnDefinition("Email", ColumnType.String, "Primary email", unique: true, defaultValue: "unknown@example.com")
            },
            new Dictionary<string, string?> { ["source"] = "crm" });
    }

    private static CollectionDefinition OrderCollection()
    {
        return new CollectionDefinition(
            "Order",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Number", ColumnType.String)
            });
    }

    private static CollectionDefinition OrderWithCustomerIdCollection()
    {
        return new CollectionDefinition(
            "Order",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("CustomerId", ColumnType.Guid),
                new ColumnDefinition("Number", ColumnType.String)
            });
    }

    private static CollectionDefinition ProfileCollection()
    {
        return new CollectionDefinition(
            "Profile",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("OwnerId", ColumnType.Guid, unique: true)
            });
    }

    private static CollectionDefinition TagCollection()
    {
        return new CollectionDefinition(
            "Tag",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("Name", ColumnType.String)
            });
    }

    private static CollectionDefinition TagLinkCollection()
    {
        return new CollectionDefinition(
            "TagLink",
            columns: new[]
            {
                new ColumnDefinition("Id", ColumnType.Guid),
                new ColumnDefinition("TagName", ColumnType.String)
            });
    }

    private static Dictionary<string, object?> CustomerRecord(
        Guid id,
        string firstName,
        string lastName,
        string email,
        string? unknown = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["FirstName"] = firstName,
            ["LastName"] = lastName,
            ["Email"] = email
        };

        if (unknown is not null)
        {
            record["Unknown"] = unknown;
        }

        return record;
    }
}
