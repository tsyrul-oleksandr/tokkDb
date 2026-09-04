using TokkDb.LLM.Core;

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

    [Fact]
    public void RegistryPersistsToDisk()
    {
        var writer = new SemanticTypeRegistry();
        writer.Register(new SemanticTypeDefinition(
            "email",
            "Email",
            "Electronic mail address",
            ColumnType.String,
            Aliases: new[] { "Email" },
            ValidationPatterns: new[] { @"^[^@\s]+@[^@\s]+\.[^@\s]+$" },
            NormalizationRules: new[] { "Trim", "ToLowerInvariant" }));

        var reader = new SemanticTypeRegistry();
        var loaded = reader.GetByNameOrAlias("Email");

        Assert.NotNull(loaded);
        Assert.Equal("email", loaded.Name);
        Assert.Contains("Trim", loaded.NormalizationRules ?? Array.Empty<string>());
        Assert.Contains(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", loaded.ValidationPatterns ?? Array.Empty<string>());
    }
}
