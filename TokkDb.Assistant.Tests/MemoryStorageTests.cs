using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The one thing about <see cref="MemoryStorage"/> that is not the contract's business, and has
/// to be tested here because the contract suite deliberately cannot see it: the fake shuffles.
///
/// The suite asserts that <c>GetAll</c> returns every record and says nothing about their order
/// (SC-4's fifth answer). A fake that happened to return insertion order would let phase 3 or
/// phase 5 quietly depend on it and discover the dependency against the engine, months later. So
/// the fake returns them in a deliberately wrong order, and this test is what keeps that guard
/// from decaying into an accident.
/// </summary>
public sealed class MemoryStorageTests
{
    [Fact]
    public void GetAll_does_not_return_the_records_in_the_order_they_were_created()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("notes", columns:
            [new ColumnDefinition("body", ColumnType.Text)]));

        var created = Enumerable.Range(0, 40)
            .Select(i => storage.Create("notes", new Dictionary<string, object?> { ["body"] = $"note {i}" }).Id)
            .ToArray();

        var returned = storage.GetAll("notes").Select(static record => record.Id).ToArray();

        Assert.Equal(created.Order(), returned.Order());
        Assert.NotEqual(created, returned);
    }

    [Fact]
    public void The_shuffle_is_not_the_same_shuffle_every_time_it_is_asked()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("notes", columns:
            [new ColumnDefinition("body", ColumnType.Text)]));

        foreach (var i in Enumerable.Range(0, 40))
        {
            storage.Create("notes", new Dictionary<string, object?> { ["body"] = $"note {i}" });
        }

        var first = storage.GetAll("notes").Select(static record => record.Id).ToArray();
        var second = storage.GetAll("notes").Select(static record => record.Id).ToArray();

        Assert.NotEqual(first, second);
    }

    /// <summary>Collections are shuffled for the same reason: nothing promises their order either.</summary>
    [Fact]
    public void The_collections_are_not_listed_in_the_order_they_were_created()
    {
        var storage = new MemoryStorage();

        var names = Enumerable.Range(0, 20).Select(i => $"thing_{i:00}").ToArray();
        foreach (var name in names)
        {
            storage.CreateCollection(new CollectionDefinition(name, columns:
                [new ColumnDefinition("body", ColumnType.Text)]));
        }

        var listed = storage.GetCollectionDefinitions().Select(static d => d.Name).ToArray();

        Assert.Equal(names.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.NotEqual(names, listed);
    }
}
