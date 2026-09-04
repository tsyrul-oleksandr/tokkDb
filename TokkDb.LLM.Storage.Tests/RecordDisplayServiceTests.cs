using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// Covers the ShowRecords resolution path: DisplayRule evaluation, additional
/// fields, ordering, and the handling of anything invalid an LLM might send.
/// </summary>
public sealed class RecordDisplayServiceTests
{
    private static RecordDisplayService CreateService()
    {
        var evaluator = new DisplayRuleEvaluator();
        return new RecordDisplayService(evaluator, new DisplayRuleValidator(evaluator));
    }

    private static MemoryStorage CreateProductStorage(out Guid[] ids, string? displayRule = "{Name} — {Brand}")
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("Product", "Products", new[]
        {
            new ColumnDefinition("Name", ColumnType.String),
            new ColumnDefinition("Brand", ColumnType.String),
            new ColumnDefinition("Price", ColumnType.Decimal),
            new ColumnDefinition("StockQuantity", ColumnType.Int32)
        }));

        if (displayRule is not null)
        {
            storage.SetDisplayRule("Product", new DisplayRule(displayRule));
        }

        var created = new[]
        {
            storage.Create("Product", new Dictionary<string, object?>
            {
                ["Name"] = "Laptop Pro 16", ["Brand"] = "Lenovo", ["Price"] = 1499m, ["StockQuantity"] = 12
            }),
            storage.Create("Product", new Dictionary<string, object?>
            {
                ["Name"] = "Wireless Mouse", ["Brand"] = "Logitech", ["Price"] = 49m, ["StockQuantity"] = 84
            }),
            storage.Create("Product", new Dictionary<string, object?>
            {
                ["Name"] = "Mechanical Keyboard", ["Brand"] = "Keychron", ["Price"] = 129m, ["StockQuantity"] = 31
            })
        };

        ids = created.Select(record => record.Id).ToArray();
        return storage;
    }

    [Fact]
    public void ShowRecordsDisplaysQueriedRecords()
    {
        var storage = CreateProductStorage(out var ids);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", ids.Select(id => id.ToString()).ToArray()));

        Assert.Equal("Product", message.CollectionName);
        Assert.Equal(3, message.Records.Count);
        Assert.False(message.IsEmpty);
        Assert.Empty(message.UnresolvedRecordIds);
    }

    [Fact]
    public void DisplayValueComesFromDisplayRule()
    {
        var storage = CreateProductStorage(out var ids);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [ids[0].ToString()]));

        Assert.Equal("Laptop Pro 16 — Lenovo", message.Records[0].DisplayValue);
    }

    [Fact]
    public void MissingDisplayRuleFallsBackDeterministically()
    {
        var storage = CreateProductStorage(out var ids, displayRule: null);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [ids[0].ToString()]));

        // Fallback is the first non-empty field value, never an LLM call.
        Assert.Equal("Laptop Pro 16", message.Records[0].DisplayValue);
    }

    [Fact]
    public void AdditionalFieldsAreDisplayedNextToDisplayValue()
    {
        var storage = CreateProductStorage(out var ids);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [ids[0].ToString()], ["Price", "StockQuantity"]));

        var item = message.Records[0];
        Assert.Equal("Laptop Pro 16 — Lenovo", item.DisplayValue);
        Assert.Collection(
            item.AdditionalFields,
            field => { Assert.Equal("Price", field.Name); Assert.Equal("1499", field.Value); },
            field => { Assert.Equal("StockQuantity", field.Name); Assert.Equal("12", field.Value); });
    }

    [Fact]
    public void RecordOrderIsPreserved()
    {
        var storage = CreateProductStorage(out var ids);
        var requested = new[] { ids[2].ToString(), ids[0].ToString(), ids[1].ToString() };

        var message = CreateService().BuildRecordsDisplay(storage, new ShowRecordsRequest("Product", requested));

        Assert.Equal(requested, message.Records.Select(record => record.RecordId).ToArray());
    }

    [Fact]
    public void DuplicateRecordIdsAreRemovedKeepingFirstOccurrence()
    {
        var storage = CreateProductStorage(out var ids);
        var requested = new[] { ids[0].ToString(), ids[1].ToString(), ids[0].ToString() };

        var message = CreateService().BuildRecordsDisplay(storage, new ShowRecordsRequest("Product", requested));

        Assert.Equal(
            new[] { ids[0].ToString(), ids[1].ToString() },
            message.Records.Select(record => record.RecordId).ToArray());
    }

    [Fact]
    public void InvalidRecordIdDoesNotCrashAndValidRecordsStillRender()
    {
        var storage = CreateProductStorage(out var ids);
        var requested = new[] { ids[0].ToString(), "not-a-guid", Guid.NewGuid().ToString() };

        var message = CreateService().BuildRecordsDisplay(storage, new ShowRecordsRequest("Product", requested));

        Assert.Single(message.Records);
        Assert.Equal(ids[0].ToString(), message.Records[0].RecordId);
        Assert.Equal(2, message.UnresolvedRecordIds.Count);
    }

    [Fact]
    public void RecordFromAnotherCollectionIsNotDisplayed()
    {
        var storage = CreateProductStorage(out _);
        storage.CreateCollection(new CollectionDefinition("Order", columns: new[]
        {
            new ColumnDefinition("Number", ColumnType.String)
        }));
        var foreign = storage.Create("Order", new Dictionary<string, object?> { ["Number"] = "A-1" });

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [foreign.Id.ToString()]));

        Assert.Empty(message.Records);
        Assert.Contains(foreign.Id.ToString(), message.UnresolvedRecordIds);
    }

    [Fact]
    public void InvalidAdditionalFieldIsReportedAndIgnored()
    {
        var storage = CreateProductStorage(out var ids);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [ids[0].ToString()], ["Price", "NoSuchColumn"]));

        Assert.Contains("NoSuchColumn", message.InvalidAdditionalFields);
        Assert.Single(message.Records[0].AdditionalFields);
        Assert.Equal("Price", message.Records[0].AdditionalFields[0].Name);
    }

    [Fact]
    public void FieldWithNoValueIsOmittedRatherThanRenderedAsNull()
    {
        var storage = CreateProductStorage(out _);
        var sparse = storage.Create("Product", new Dictionary<string, object?>
        {
            ["Name"] = "Cable", ["Brand"] = "Anker"
        });

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [sparse.Id.ToString()], ["Price", "StockQuantity"]));

        Assert.Equal("Cable — Anker", message.Records[0].DisplayValue);
        Assert.DoesNotContain(
            message.Records[0].AdditionalFields,
            field => field.Value.Contains("null", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyRecordIdsProducesEmptyState()
    {
        var storage = CreateProductStorage(out _);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", Array.Empty<string>()));

        Assert.True(message.IsEmpty);
        Assert.Empty(message.Records);
    }

    [Fact]
    public void UnknownCollectionProducesEmptyStateWithoutThrowing()
    {
        var storage = CreateProductStorage(out var ids);

        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("NoSuchCollection", [ids[0].ToString()]));

        Assert.True(message.IsEmpty);
        Assert.Equal("NoSuchCollection", message.CollectionName);
    }

    [Fact]
    public void RecordDisplayModelIsProviderIndependent()
    {
        var storage = CreateProductStorage(out var ids);
        var message = CreateService().BuildRecordsDisplay(
            storage,
            new ShowRecordsRequest("Product", [ids[0].ToString()], ["Price"]));

        // The display model must not drag in any provider or agent-framework
        // assembly, so it renders identically for OpenAI, Ollama and others.
        foreach (var type in new[]
                 {
                     message.GetType(),
                     message.Records[0].GetType(),
                     message.Records[0].AdditionalFields[0].GetType()
                 })
        {
            var assembly = type.Assembly.GetName().Name ?? string.Empty;
            Assert.DoesNotContain("OpenAI", assembly, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ollama", assembly, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Microsoft.Agents", assembly, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Microsoft.Extensions.AI", assembly, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// The navigation contract that carries a clicked display value to the Database page.
/// </summary>
public sealed class RecordNavigationServiceTests
{
    private static RecordNavigationService CreateService() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordNavigationService>.Instance);

    [Fact]
    public void ClickingDisplayValueRaisesNavigationWithCollectionAndRecordId()
    {
        var service = CreateService();
        OpenRecordRequest? received = null;
        service.RecordNavigationRequested += (_, request) => received = request;

        service.OpenRecord(new OpenRecordRequest("Product", "123"));

        Assert.NotNull(received);
        Assert.Equal("Product", received!.CollectionName);
        Assert.Equal("123", received.RecordId);
    }

    [Fact]
    public void IncompleteNavigationRequestIsIgnored()
    {
        var service = CreateService();
        var raised = 0;
        service.RecordNavigationRequested += (_, _) => raised++;

        service.OpenRecord(new OpenRecordRequest("Product", " "));
        service.OpenRecord(new OpenRecordRequest(" ", "123"));

        Assert.Equal(0, raised);
    }
}
