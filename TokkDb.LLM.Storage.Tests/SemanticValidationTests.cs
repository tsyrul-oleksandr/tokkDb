using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// Semantic types as column refinements: the rules a value must satisfy, and
/// what a caller is told when it does not.
/// </summary>
public sealed class SemanticValidationTests
{
    /// <summary>An Email type written the old way, with a bare pattern.</summary>
    private static SemanticTypeDefinition Email() =>
        new(
            "Email",
            "Email address",
            "An email address",
            ColumnType.String,
            Examples: new[] { "olena@example.com", "a.b@shop.co.uk" },
            ValidationPattern: @"^[\w\-\.]+@([\w\-]+\.)+[\w\-]{2,4}$");

    /// <summary>An age type written the new way, as bounds.</summary>
    private static SemanticTypeDefinition HumanAge() =>
        new(
            "HumanAge",
            "Human age",
            "Age of a person in years",
            ColumnType.Int32,
            Validations: new[]
            {
                new SemanticValidation(SemanticValidationKind.MinValue, Value: "0"),
                new SemanticValidation(SemanticValidationKind.MaxValue, Value: "120")
            });

    private static MemoryStorage BuildUsers(params SemanticTypeDefinition[] semanticTypes)
    {
        var registry = new SemanticTypeRegistry();
        foreach (var semanticType in semanticTypes)
        {
            registry.Register(semanticType);
        }

        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition("Users", "Users", new[]
        {
            new ColumnDefinition("Name", ColumnType.String),
            new ColumnDefinition("Email", ColumnType.String, semanticTypeName: "Email"),
            new ColumnDefinition("Age", ColumnType.Int32, semanticTypeName: "HumanAge")
        }));

        return storage;
    }

    private static IReadOnlyList<StorageValidationError> ErrorsFrom(
        MemoryStorage storage,
        object? email,
        object? age)
    {
        var thrown = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Users", new Dictionary<string, object?>
            {
                ["Name"] = "Oleksandr", ["Email"] = email, ["Age"] = age
            }));

        return thrown.Errors.ToArray();
    }

    // =====================================================================
    // Enforcement
    // =====================================================================

    [Fact]
    public void AValueThatBreaksThePatternIsRefusedBeforeItIsStored()
    {
        var storage = BuildUsers(Email(), HumanAge());

        var errors = ErrorsFrom(storage, "124@gmail.", 30);

        var error = Assert.Single(errors);
        Assert.Equal("InvalidSemanticValue", error.Code);
        Assert.Equal("Email", error.ColumnName);
        Assert.Empty(storage.GetAll("Users"));
    }

    [Fact]
    public void NumericBoundsAcceptWhatIsInRangeAndRefuseWhatIsNot()
    {
        var storage = BuildUsers(Email(), HumanAge());

        // A numeric semantic type used to reject every value, valid ones
        // included, because rules were only ever matched against text.
        storage.Create("Users", new Dictionary<string, object?>
        {
            ["Name"] = "Oleksandr", ["Email"] = "a@b.co", ["Age"] = 30
        });
        Assert.Single(storage.GetAll("Users"));

        Assert.Contains(
            ErrorsFrom(storage, "a@b.co", 500),
            error => error.Code == "InvalidSemanticValue" && error.ColumnName == "Age");

        Assert.Contains(
            ErrorsFrom(storage, "a@b.co", -1),
            error => error.Code == "InvalidSemanticValue" && error.ColumnName == "Age");
    }

    [Fact]
    public void EveryFailingColumnIsReportedTogether()
    {
        var storage = BuildUsers(Email(), HumanAge());

        var errors = ErrorsFrom(storage, "124@gmail.", 500);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.ColumnName == "Email");
        Assert.Contains(errors, error => error.ColumnName == "Age");
    }

    [Fact]
    public void AWronglyTypedValueIsReportedAsATypeFaultAndNotAlsoAsASemanticOne()
    {
        var storage = BuildUsers(Email(), HumanAge());

        // Text in an integer column: one fault, stated once.
        var errors = ErrorsFrom(storage, "a@b.co", "thirty");

        var error = Assert.Single(errors);
        Assert.Equal("InvalidType", error.Code);
        Assert.Equal("Age", error.ColumnName);
    }

    // =====================================================================
    // What the caller is told
    // =====================================================================

    [Fact]
    public void TheMessageNamesTheValueTheRuleAndAnExample()
    {
        var storage = BuildUsers(Email(), HumanAge());

        var message = Assert.Single(ErrorsFrom(storage, "124@gmail.", 30)).Message;

        Assert.Contains("'124@gmail.'", message, StringComparison.Ordinal);
        Assert.Contains("must match the pattern", message, StringComparison.Ordinal);
        Assert.Contains("olena@example.com", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageStatesTheBoundThatWasBroken()
    {
        var storage = BuildUsers(Email(), HumanAge());

        Assert.Contains(
            "must be 120 or less",
            Assert.Single(ErrorsFrom(storage, "a@b.co", 500), error => error.ColumnName == "Age").Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "must be 0 or greater",
            Assert.Single(ErrorsFrom(storage, "a@b.co", -1), error => error.ColumnName == "Age").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ALongValueIsTruncatedInTheMessage()
    {
        var storage = BuildUsers(Email(), HumanAge());

        var message = Assert.Single(ErrorsFrom(storage, new string('x', 500), 30)).Message;

        Assert.Contains("...", message, StringComparison.Ordinal);
        Assert.True(message.Length < 400, $"Message was {message.Length} characters long.");
    }

    // =====================================================================
    // Rules that could never hold are refused when the type is defined
    // =====================================================================

    [Fact]
    public void APatternCannotBeAttachedToANumericType()
    {
        var registry = new SemanticTypeRegistry();

        var thrown = Assert.Throws<ArgumentException>(() => registry.Register(new SemanticTypeDefinition(
            "Age", "Age", "Age", ColumnType.Int32,
            Validations: new[] { new SemanticValidation(SemanticValidationKind.Regex, Pattern: @"^\d+$") })));

        Assert.Contains("cannot be used with base type Int32", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundCannotBeAttachedToATextType()
    {
        var registry = new SemanticTypeRegistry();

        var thrown = Assert.Throws<ArgumentException>(() => registry.Register(new SemanticTypeDefinition(
            "Nickname", "Nickname", "Nickname", ColumnType.String,
            Validations: new[] { new SemanticValidation(SemanticValidationKind.MaxValue, Value: "10") })));

        Assert.Contains("cannot be used with base type String", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleMissingItsParameterIsRefused()
    {
        var registry = new SemanticTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new SemanticTypeDefinition(
            "Age", "Age", "Age", ColumnType.Int32,
            Validations: new[] { new SemanticValidation(SemanticValidationKind.MinValue) })));

        Assert.Throws<ArgumentException>(() => registry.Register(new SemanticTypeDefinition(
            "Code", "Code", "Code", ColumnType.String,
            Validations: new[] { new SemanticValidation(SemanticValidationKind.MaxLength) })));
    }

    [Fact]
    public void ABoundThatIsNotOfTheBaseTypeIsRefused()
    {
        var registry = new SemanticTypeRegistry();

        var thrown = Assert.Throws<ArgumentException>(() => registry.Register(new SemanticTypeDefinition(
            "Age", "Age", "Age", ColumnType.Int32,
            Validations: new[] { new SemanticValidation(SemanticValidationKind.MinValue, Value: "young") })));

        Assert.Contains("not a valid Int32", thrown.Message, StringComparison.Ordinal);
    }

    // =====================================================================
    // The older pattern properties keep working
    // =====================================================================

    [Fact]
    public void LegacyPatternsBecomeRegexRulesAndAreStillReadable()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(Email());

        var stored = registry.GetByNameOrAlias("Email");

        Assert.NotNull(stored);
        var rule = Assert.Single(stored.Validations!);
        Assert.Equal(SemanticValidationKind.Regex, rule.Kind);

        // The old properties are left as they were, so anything reading them
        // sees what it always saw.
        Assert.Contains(
            @"^[\w\-\.]+@([\w\-]+\.)+[\w\-]{2,4}$",
            stored.ValidationPatterns!,
            StringComparer.Ordinal);
    }

    [Fact]
    public void APatternGivenTwiceProducesOneRule()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "Code", "Code", "A code", ColumnType.String,
            ValidationPattern: "^[A-Z]+$",
            Validations: new[] { new SemanticValidation(SemanticValidationKind.Regex, Pattern: "^[A-Z]+$") }));

        Assert.Single(registry.GetByNameOrAlias("Code")!.Validations!);
    }

    [Fact]
    public void LengthRulesApplyToText()
    {
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "ShortCode", "Short code", "Two to four letters", ColumnType.String,
            Validations: new[]
            {
                new SemanticValidation(SemanticValidationKind.MinLength, Length: 2),
                new SemanticValidation(SemanticValidationKind.MaxLength, Length: 4)
            }));

        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition("Items", "Items", new[]
        {
            new ColumnDefinition("Code", ColumnType.String, semanticTypeName: "ShortCode")
        }));

        storage.Create("Items", new Dictionary<string, object?> { ["Code"] = "ABC" });

        var thrown = Assert.Throws<StorageValidationException>(() =>
            storage.Create("Items", new Dictionary<string, object?> { ["Code"] = "ABCDE" }));

        Assert.Contains(
            "must be at most 4 character(s) long",
            thrown.Errors.Single().Message,
            StringComparison.Ordinal);
    }
}
