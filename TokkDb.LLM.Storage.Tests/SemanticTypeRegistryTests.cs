using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

public sealed class SemanticTypeRegistryTests
{
    [Fact]
    public void RegisterAndResolveByAliasWorks()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "email",
            "Email",
            "Electronic mail address",
            ColumnType.String,
            Aliases: new[] { "Email", "E-mail" }));

        var resolved = registry.GetByNameOrAlias("E-mail");

        Assert.NotNull(resolved);
        Assert.Equal("email", resolved.Name);
        Assert.Equal(ColumnType.String, resolved.BaseType);
    }

    /// <summary>
    /// Two registries over one store see the same definitions. This is what the test of the
    /// same name used to claim: it built two registries that shared nothing and expected the
    /// second to see the first's work, so it could only ever have passed if a registry kept
    /// its definitions in a static — and it never did pass. The persistence it was named for
    /// is <see cref="TokkDbSemanticTypeStoreTests"/>, against a real file.
    /// </summary>
    [Fact]
    public void TwoRegistriesOverOneStoreSeeTheSameDefinitions()
    {
        var store = new InMemorySemanticTypeStore();
        var writer = new SemanticTypeRegistry(store);
        writer.Register(new SemanticTypeDefinition(
            "email",
            "Email",
            "Electronic mail address",
            ColumnType.String,
            Aliases: new[] { "Email" },
            ValidationPatterns: new[] { @"^[^@\s]+@[^@\s]+\.[^@\s]+$" },
            NormalizationRules: new[] { "Trim", "ToLowerInvariant" }));

        var reader = new SemanticTypeRegistry(store);
        var loaded = reader.GetByNameOrAlias("Email");

        Assert.NotNull(loaded);
        Assert.Equal("email", loaded.Name);
        Assert.Contains("Trim", loaded.NormalizationRules ?? Array.Empty<string>());
        Assert.Contains(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", loaded.ValidationPatterns ?? Array.Empty<string>());
    }

    /// <summary>
    /// Registering a name twice replaces it. Without a store the second definition merely
    /// shadowed the first in every lookup; with one, an append would leave two documents for
    /// one name and the next restart would load whichever came off the pages first.
    /// </summary>
    [Fact]
    public void RegisteringANameAgainReplacesTheDefinitionRatherThanShadowingIt()
    {
        var store = new InMemorySemanticTypeStore();
        var registry = new SemanticTypeRegistry(store);
        registry.Register(new SemanticTypeDefinition("email", "Email", "First", ColumnType.String));
        registry.Register(new SemanticTypeDefinition("email", "Email", "Second", ColumnType.String));

        Assert.Equal("Second", Assert.Single(registry.GetAll()).Description);
        Assert.Equal("Second", Assert.Single(store.Load()).Description);
        Assert.Equal("Second", new SemanticTypeRegistry(store).GetByNameOrAlias("email")!.Description);
    }

    [Fact]
    public void ADeletedTypeIsGoneFromTheStoreAsWellAsTheRegistry()
    {
        var store = new InMemorySemanticTypeStore();
        var registry = new SemanticTypeRegistry(store);
        registry.Register(new SemanticTypeDefinition("email", "Email", "Electronic mail", ColumnType.String));

        Assert.True(registry.Delete("email"));

        Assert.Empty(store.Load());
        Assert.Empty(new SemanticTypeRegistry(store).GetAll());
    }

    /// <summary>
    /// A type something else derives from stays, and stays in the store: the child's base type
    /// was checked against it when the child was registered and nothing would check it again.
    /// </summary>
    [Fact]
    public void ATypeThatIsAParentIsNotDeletedFromTheStoreEither()
    {
        var store = new InMemorySemanticTypeStore();
        var registry = new SemanticTypeRegistry(store);
        registry.Register(new SemanticTypeDefinition("contact", "Contact", "Any contact", ColumnType.String));
        registry.Register(new SemanticTypeDefinition(
            "email", "Email", "Electronic mail", ColumnType.String, ParentType: "contact"));

        Assert.False(registry.Delete("contact"));

        Assert.Equal(2, store.Load().Count);
        Assert.NotNull(new SemanticTypeRegistry(store).GetByNameOrAlias("contact"));
    }
}
