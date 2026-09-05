using TokkDb.LLM.Core;
using TokkDb.LLM.Storage.Engine;

namespace TokkDb.LLM.Storage.Tests;

/// <summary>
/// Phase 4's exit condition: the covered subset of IStorage works against a real database
/// file rather than a dictionary in memory. Deliberately small — the skeleton it exercises
/// is throwaway, and what it is for is showing where the two contracts disagree.
/// </summary>
public sealed class TokkDbStorageSmokeTests : IDisposable
{
    private readonly string _databaseFilePath =
        Path.Combine(Path.GetTempPath(), $"tokkdb-storage-{Ulid.NewUlid()}.db");

    private static CollectionDefinition Article() => new(
        "Article",
        "A publication record",
        [
            new ColumnDefinition("Title", ColumnType.String, "The title as published"),
            new ColumnDefinition("Year", ColumnType.Int32),
            new ColumnDefinition("Reviewed", ColumnType.Boolean)
        ]);

    [Fact]
    public void ARecordIsCreatedReadUpdatedAndDeletedThroughIStorage()
    {
        var id = Ulid.Empty;

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            storage.CreateCollection(Article());

            var created = storage.Create("Article", new Dictionary<string, object?>
            {
                ["Title"] = "Проєктування СКБД",
                ["Year"] = 2026,
                ["Reviewed"] = false
            });

            id = created.Id;
            Assert.NotEqual(Ulid.Empty, id);
        }

        // Reopened, so what follows reads what was actually written to the file.
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var read = storage.GetById("Article", id);
            Assert.NotNull(read);
            Assert.Equal("Проєктування СКБД", read!.Fields["Title"]);
            Assert.Equal(2026, read.Fields["Year"]);
            Assert.Equal(false, read.Fields["Reviewed"]);

            Assert.True(storage.Update(new StorageRecord(id, "Article", new Dictionary<string, object?>
            {
                ["Title"] = "Проєктування СКБД, видання друге",
                ["Year"] = 2027,
                ["Reviewed"] = true
            })));
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var updated = Assert.Single(storage.GetAll("Article"));
            Assert.Equal(id, updated.Id);
            Assert.Equal("Проєктування СКБД, видання друге", updated.Fields["Title"]);
            Assert.Equal(2027, updated.Fields["Year"]);
            Assert.Equal(true, updated.Fields["Reviewed"]);

            Assert.True(storage.Delete("Article", id));
            Assert.False(storage.Delete("Article", id));
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            Assert.Empty(storage.GetAll("Article"));
            Assert.Null(storage.GetById("Article", id));
        }
    }

    [Fact]
    public void ACollectionDefinitionSurvivesAReopen()
    {
        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            storage.CreateCollection(Article());
        }

        using (var storage = new TokkDbStorage(_databaseFilePath))
        {
            var definition = storage.GetCollectionDefinition("Article");
            Assert.NotNull(definition);
            Assert.Equal("A publication record", definition!.Description);
            Assert.Equal(
                ["Title", "Year", "Reviewed"],
                definition.Columns.Select(column => column.Name).ToArray());
            Assert.Equal(
                [ColumnType.String, ColumnType.Int32, ColumnType.Boolean],
                definition.Columns.Select(column => column.Type).ToArray());
            Assert.Equal("The title as published", definition.Columns.First().Description);

            // The engine's own system collections stay on its side of the boundary.
            Assert.Equal(["Article"], storage.GetCollectionDefinitions().Select(item => item.Name).ToArray());
        }
    }

    [Fact]
    public void MembersOutsideTheSkeletonSaySoRatherThanReturningNothing()
    {
        using var storage = new TokkDbStorage(_databaseFilePath);
        storage.CreateCollection(Article());

        Assert.Throws<NotSupportedException>(() => storage.DeleteCollection("Article"));
        Assert.Throws<NotSupportedException>(() => storage.GetRelations());
        Assert.Throws<NotSupportedException>(
            () => storage.AddColumn("Article", new ColumnDefinition("Doi", ColumnType.String)));
    }

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
