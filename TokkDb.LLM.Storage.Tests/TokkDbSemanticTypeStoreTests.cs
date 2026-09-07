using Microsoft.Extensions.DependencyInjection;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// D-4's §4.9 obligation, checked the only way it can be: register things, close the file,
/// open it again and look. The done-when of this step is exactly this — semantic types and
/// display rules registered in one run are present after a restart.
/// </summary>
public sealed class TokkDbSemanticTypeStoreTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-semantic-{Ulid.NewUlid()}.db");

    private static SemanticTypeDefinition Email() => new(
        "email",
        "Email",
        "Electronic mail address",
        ColumnType.String,
        Aliases: ["Email", "E-mail"],
        Examples: ["olena@example.com"],
        ValidationPatterns: [@"^[^@\s]+@[^@\s]+\.[^@\s]+$"],
        NormalizationRules: ["Trim", "ToLowerInvariant"],
        Validations: [new SemanticValidation(SemanticValidationKind.MaxLength, Length: 254)]);

    private static CollectionDefinition Customer() => new(
        "Customer",
        "Stores customer records",
        [
            new ColumnDefinition("FullName", ColumnType.String),
            new ColumnDefinition("Email", ColumnType.String, semanticTypeName: "email")
        ],
        new Dictionary<string, string?> { ["derivedBy"] = "model", ["confidence"] = "0.86" });

    /// <summary>The done-when, in one test.</summary>
    [Fact]
    public void SemanticTypesAndDisplayRulesRegisteredInOneRunArePresentAfterRestart()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var registry = new SemanticTypeRegistry(storage.SemanticTypes);
            registry.Register(Email());
            storage.CreateCollection(Customer());
            storage.SetDisplayRule("Customer", new DisplayRule("{FullName} <{Email}>"));
        }

        using (var reopened = new TokkDbStorage(_databaseFilePath))
        {
            var registry = new SemanticTypeRegistry(reopened.SemanticTypes);

            var email = registry.GetByNameOrAlias("E-mail");
            Assert.NotNull(email);
            Assert.Equal("email", email!.Name);
            Assert.Equal("Email", email.DisplayName);
            Assert.Equal("Electronic mail address", email.Description);
            Assert.Equal(ColumnType.String, email.BaseType);
            Assert.Equal(["Email", "E-mail"], email.Aliases!);
            Assert.Equal(["olena@example.com"], email.Examples!);
            Assert.Contains(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", email.ValidationPatterns!);
            Assert.Equal(["Trim", "ToLowerInvariant"], email.NormalizationRules!);
            //Both rules: the one written as a validation and the pattern folded into one.
            Assert.Contains(email.Validations!,
                rule => rule is { Kind: SemanticValidationKind.MaxLength, Length: 254 });
            Assert.Contains(email.Validations!, rule => rule.Kind == SemanticValidationKind.Regex);

            Assert.Equal("{FullName} <{Email}>",
                reopened.GetCollectionDefinition("Customer")!.DisplayRule?.Template);
            //A column's semantic type is a property of the column, so it came back with it.
            Assert.Equal("email", reopened.GetCollectionDefinition("Customer")!.Columns
                .First(column => column.Name == "Email").SemanticTypeName);
        }
    }

    /// <summary>
    /// The separation D-4 asks for: metadata the model rewrites lives in its own document, so
    /// rewriting it does not touch the schema. The schema version is what a record is read
    /// against (VR-11), and moving it because a confidence score changed would say every
    /// stored record now means something else.
    /// </summary>
    [Fact]
    public void RewritingModelMetadataDoesNotTouchTheStructuralSchema()
    {
        using var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Customer());
        var before = SchemaVersion(storage);

        for (var round = 0; round < 5; round++)
        {
            storage.SetMetadata("Customer", new Dictionary<string, string?>
            {
                ["derivedBy"] = "model",
                ["confidence"] = $"0.9{round}",
                ["lastReviewed"] = $"2026-09-0{round + 1}"
            });
            new SemanticTypeRegistry(storage.SemanticTypes).Register(
                Email() with { Description = $"Electronic mail address, revision {round}" });
        }

        Assert.Equal(before, SchemaVersion(storage));
        Assert.Equal("0.94", storage.GetCollectionDefinition("Customer")!.Metadata["confidence"]);

        //And a structural change does move it, so the version means something.
        storage.AddColumn("Customer", new ColumnDefinition("Phone", ColumnType.String));
        Assert.NotEqual(before, SchemaVersion(storage));
        //The metadata survived the schema change untouched.
        Assert.Equal("0.94", storage.GetCollectionDefinition("Customer")!.Metadata["confidence"]);
    }

    /// <summary>
    /// A hierarchy comes back in an order the registry can validate. Storage has no order of
    /// its own worth relying on — a rewritten document moves — so loading the child before
    /// the parent would have the registry refuse a hierarchy it had already accepted.
    /// </summary>
    [Fact]
    public void AHierarchyIsLoadedParentsFirstWhateverOrderItWasWrittenIn()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var registry = new SemanticTypeRegistry(storage.SemanticTypes);
            registry.Register(new SemanticTypeDefinition("contact", "Contact", "Any contact", ColumnType.String));
            registry.Register(new SemanticTypeDefinition(
                "email", "Email", "Electronic mail", ColumnType.String, ParentType: "contact"));
            registry.Register(new SemanticTypeDefinition(
                "workEmail", "Work email", "Email at work", ColumnType.String, ParentType: "email"));
            //Rewriting the root moves its document to the end of the pages, so the order the
            //documents come back in is no longer the order they were declared in.
            registry.Register(new SemanticTypeDefinition(
                "contact", "Contact", "Any way of reaching someone", ColumnType.String));
        }

        using var reopened = new TokkDbStorage(_databaseFilePath);
        var loaded = reopened.SemanticTypes.Load().Select(definition => definition.Name).ToList();

        Assert.Equal(3, loaded.Count);
        Assert.True(loaded.IndexOf("contact") < loaded.IndexOf("email"),
            $"expected contact before email, got {string.Join(", ", loaded)}");
        Assert.True(loaded.IndexOf("email") < loaded.IndexOf("workEmail"),
            $"expected email before workEmail, got {string.Join(", ", loaded)}");
        //And the registry accepts what it is handed, which is what the ordering is for.
        Assert.Equal(3, new SemanticTypeRegistry(reopened.SemanticTypes).GetAll().Count);
    }

    [Fact]
    public void ADeletedSemanticTypeStaysDeletedAcrossARestart()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var registry = new SemanticTypeRegistry(storage.SemanticTypes);
            registry.Register(Email());
            registry.Register(new SemanticTypeDefinition("phone", "Phone", "Telephone", ColumnType.String));
            Assert.True(registry.Delete("email"));
        }

        using var reopened = new TokkDbStorage(_databaseFilePath);
        var registry2 = new SemanticTypeRegistry(reopened.SemanticTypes);

        Assert.Null(registry2.GetByNameOrAlias("email"));
        Assert.NotNull(registry2.GetByNameOrAlias("phone"));
    }

    /// <summary>
    /// Re-registering a name replaces its document rather than adding a second one. Two
    /// documents for one name would make what a restart loads depend on which came off the
    /// pages first.
    /// </summary>
    [Fact]
    public void ReRegisteringATypeLeavesOneDocumentForIt()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var registry = new SemanticTypeRegistry(storage.SemanticTypes);
            for (var round = 0; round < 10; round++)
            {
                registry.Register(Email() with { Description = $"Revision {round}" });
            }
        }

        using var reopened = new TokkDbStorage(_databaseFilePath);
        var definition = Assert.Single(reopened.SemanticTypes.Load());

        Assert.Equal("Revision 9", definition.Description);
    }

    /// <summary>
    /// D-4 and DC-7: <c>_semanticTypes</c> describes its own documents, the way the engine's
    /// own system collections do, so what a semantic type looks like is readable from the
    /// catalogue rather than only from the class that writes it.
    /// </summary>
    [Fact]
    public void TheSemanticTypeCollectionDescribesItsOwnDocuments()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            new SemanticTypeRegistry(storage.SemanticTypes).Register(Email());
        }

        //Read through the engine, because the shape of a system collection is not something
        //IStorage shows — GetCollectionDefinitions deliberately hides the reserved ones.
        using var connection = new TokkDb.TokkDbConnection(_databaseFilePath);
        connection.Load();
        var columns = connection.Collection("_semanticTypes").Columns.Select(column => column.Name).ToArray();

        Assert.Contains("name", columns);
        Assert.Contains("baseType", columns);
        Assert.Contains("validations", columns);
    }

    /// <summary>
    /// The reserved collections are the engine's, so nothing the agent tools list includes
    /// one — a semantic type is not a collection of records the model may query.
    /// </summary>
    [Fact]
    public void TheSystemCollectionsAreNotListedAsCollections()
    {
        using var storage = new TokkDbStorage(_databaseFilePath);
        new SemanticTypeRegistry(storage.SemanticTypes).Register(Email());
        storage.CreateCollection(Customer());

        Assert.Equal(["Customer"], storage.GetCollectionDefinitions().Select(item => item.Name));
    }

    /// <summary>
    /// The wiring, because that is where this can silently go wrong: a registry left on the
    /// in-memory store keeps working and loses everything at the next restart, so the failure
    /// never shows up in the run that caused it.
    /// </summary>
    [Fact]
    public void TheRegisteredRegistryIsTheOneBackedByTheDatabase()
    {
        using (var provider = new ServiceCollection().AddTokkDbStorage(_databaseFilePath).BuildServiceProvider())
        {
            provider.GetRequiredService<ISemanticTypeRegistry>().Register(Email());
            //One database, opened once: the registry and the storage are the same connection.
            Assert.Same(provider.GetRequiredService<IStorage>(), provider.GetRequiredService<TokkDbStorage>());
            provider.GetRequiredService<TokkDbStorage>().Dispose();
        }

        using var reopened = new ServiceCollection()
            .AddTokkDbStorage(_databaseFilePath)
            .BuildServiceProvider();
        Assert.NotNull(reopened.GetRequiredService<ISemanticTypeRegistry>().GetByNameOrAlias("E-mail"));
        reopened.GetRequiredService<TokkDbStorage>().Dispose();
    }

    private static int SchemaVersion(TokkDbStorage storage) => storage.SchemaVersion("Customer");

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
