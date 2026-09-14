using TokkDb.Assistant.Storage;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The six version operations undo needs (SC-12, SC-12a; AJ-2 of the versioning plan), the same
/// over both backends: what a version is, what "touched since" means, what a diff shows, what a
/// restore is judged by, and what a purge and an erase leave.
/// </summary>
public abstract partial class StorageContractTests
{
    private const string Notes = "notes";

    private IStorage GivenNotes()
    {
        var storage = Storage;

        storage.CreateCollection(new CollectionDefinition(Notes, "things I wrote down", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is about", required: true),
            new ColumnDefinition("words", ColumnType.Integer, "how long it is")
        ]));

        return storage;
    }

    private static Dictionary<string, object?> Note(string title, long words = 10) => new()
    {
        ["title"] = title, ["words"] = words
    };

    /// <summary>
    /// The head follows every write. Inside a unit of work, the version an update produced is
    /// already the head; after a delete, the head is the deletion, and every version along the
    /// way is still kept.
    /// </summary>
    [Fact]
    public void The_head_version_follows_creates_updates_and_deletes()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("first"));

        var created = storage.HeadVersion(Notes, record.Id);
        Assert.NotNull(created);

        Ulid? insideTheUnitOfWork = null;
        storage.InUnitOfWork(() =>
        {
            storage.Update(record.With("title", "second"));
            insideTheUnitOfWork = storage.HeadVersion(Notes, record.Id);
        });
        var updated = storage.HeadVersion(Notes, record.Id);

        Assert.NotNull(updated);
        Assert.Equal(insideTheUnitOfWork, updated);
        Assert.NotEqual(created, updated);
        Assert.True(updated!.Value.CompareTo(created!.Value) > 0, "versions sort in the order they were made");

        storage.Delete(Notes, record.Id);
        var deleted = storage.HeadVersion(Notes, record.Id);

        Assert.NotNull(deleted);
        Assert.NotEqual(updated, deleted);
        Assert.Null(storage.GetById(Notes, record.Id));

        Assert.True(storage.Keeps(Notes, record.Id, created.Value));
        Assert.True(storage.Keeps(Notes, record.Id, updated.Value));
        Assert.True(storage.Keeps(Notes, record.Id, deleted.Value));
        Assert.False(storage.Keeps(Notes, record.Id, Ulid.NewUlid()));
        Assert.Null(storage.HeadVersion(Notes, Ulid.NewUlid()));
    }

    /// <summary>A diff is column by column, old beside new; against a deletion, everything is a change to nothing.</summary>
    [Fact]
    public void A_diff_lists_the_changed_columns_old_beside_new()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("draft", 12));
        var first = storage.HeadVersion(Notes, record.Id)!.Value;

        storage.Update(record.With("words", 40L));
        var second = storage.HeadVersion(Notes, record.Id)!.Value;

        var difference = storage.DiffVersions(Notes, record.Id, first, second);

        var change = Assert.Single(difference.Changes);
        Assert.Equal("words", change.ColumnName);
        Assert.Equal(12L, change.Before);
        Assert.Equal(40L, change.After);
        Assert.False(difference.FromIsDeleted);
        Assert.False(difference.ToIsDeleted);

        storage.Delete(Notes, record.Id);
        var gone = storage.HeadVersion(Notes, record.Id)!.Value;

        var toNothing = storage.DiffVersions(Notes, record.Id, second, gone);

        Assert.True(toNothing.ToIsDeleted);
        Assert.Equal(["title", "words"], toNothing.Changes.Select(static column => column.ColumnName));
        Assert.All(toNothing.Changes, static column => Assert.Null(column.After));
        Assert.Equal("draft", toNothing.Changes[0].Before);
    }

    /// <summary>A restore puts the record back as it was, under its own identity, and is itself a version.</summary>
    [Fact]
    public void A_restore_puts_the_record_back_under_its_identity()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("first", 1));
        var first = storage.HeadVersion(Notes, record.Id)!.Value;
        storage.Update(record.With("title", "second").With("words", 2L));
        var second = storage.HeadVersion(Notes, record.Id)!.Value;

        var restored = storage.RestoreVersion(Notes, record.Id, first);

        Assert.Equal(record.Id, restored.Id);
        Assert.Equal("first", restored["title"]);
        Assert.Equal(1L, restored["words"]);
        Assert.Equal(restored, storage.GetById(Notes, record.Id));
        var third = storage.HeadVersion(Notes, record.Id)!.Value;
        Assert.NotEqual(second, third);
        Assert.True(storage.Keeps(Notes, record.Id, second));

        // Already there: nothing changes.
        Assert.Equal(restored, storage.RestoreVersion(Notes, record.Id, third));
        Assert.Equal(third, storage.HeadVersion(Notes, record.Id));

        // A deletion is not a state to restore to.
        storage.Delete(Notes, record.Id);
        var gone = storage.HeadVersion(Notes, record.Id)!.Value;
        Assert.Throws<VersionNotRestorableException>(() => storage.RestoreVersion(Notes, record.Id, gone));

        // But the version before it is, and the record comes back into the collection.
        var back = storage.RestoreVersion(Notes, record.Id, third);
        Assert.Equal(record.Id, back.Id);
        Assert.Equal("first", back["title"]);
        Assert.Single(storage.GetAll(Notes));
    }

    /// <summary>
    /// A deleted record restored under its own identity is the record the references pointed at:
    /// an expense created for a conference resolves it again after it comes back.
    /// </summary>
    [Fact]
    public void A_restored_record_is_the_one_references_point_at()
    {
        var storage = GivenConferencesAndExpenses();
        var conference = AConference(storage, "PyCon");
        var version = storage.HeadVersion(Conferences, conference)!.Value;

        storage.Delete(Conferences, conference);
        Assert.Throws<StorageValidationException>(() => storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["what"] = "tickets", ["conference"] = "PyCon", ["amount_eur"] = 100m
        }));

        var restored = storage.RestoreVersion(Conferences, conference, version);

        Assert.Equal(conference, restored.Id);
        storage.Create(Expenses, new Dictionary<string, object?> { ["what"] = "tickets", ["conference"] = "PyCon", ["amount_eur"] = 100m });
        Assert.Single(storage.GetAll(Expenses));
    }

    /// <summary>
    /// The restore is judged as a write is (SC-3, SC-8): a value another record has taken since
    /// is a duplicate naming that record, a reference to a record that is gone is missing, and
    /// nothing is written when either refuses.
    /// </summary>
    [Fact]
    public void A_restore_is_refused_like_a_write_when_the_world_has_moved_on()
    {
        var storage = GivenConferencesAndExpenses();
        var first = storage.Create(Conferences, new Dictionary<string, object?> { ["name"] = "X", ["city"] = "Prague" });
        var wasX = storage.HeadVersion(Conferences, first.Id)!.Value;
        storage.Update(first.With("name", "Y"));
        var isY = storage.HeadVersion(Conferences, first.Id)!.Value;
        var second = storage.Create(Conferences, new Dictionary<string, object?> { ["name"] = "X", ["city"] = "Brno" });

        var duplicate = Assert.Throws<StorageValidationException>(() => storage.RestoreVersion(Conferences, first.Id, wasX));

        var error = Assert.IsType<DuplicateValue>(Assert.Single(duplicate.Errors));
        Assert.Equal("name", error.ColumnName);
        Assert.Equal(second.Id, error.HeldBy);
        Assert.Equal("Y", storage.GetById(Conferences, first.Id)!["name"]);
        Assert.Equal(isY, storage.HeadVersion(Conferences, first.Id));

        var expense = storage.Create(Expenses, new Dictionary<string, object?> { ["what"] = "tickets", ["conference"] = "Y", ["amount_eur"] = 5m });
        var forY = storage.HeadVersion(Expenses, expense.Id)!.Value;
        storage.Update(expense.With("conference", "X"));
        storage.Update(first.With("name", "Z"));

        var missing = Assert.Throws<StorageValidationException>(() => storage.RestoreVersion(Expenses, expense.Id, forY));

        var reference = Assert.IsType<ReferenceMissing>(Assert.Single(missing.Errors));
        Assert.Equal("conference", reference.ColumnName);
        Assert.Equal("X", storage.GetById(Expenses, expense.Id)!["conference"]);
    }

    /// <summary>
    /// A purge before a moment keeps the version the record was at then and everything after it;
    /// what went is no longer kept, and every operation that needs it says so.
    /// </summary>
    [Fact]
    public void A_purged_version_is_no_longer_kept()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("first"));
        var first = storage.HeadVersion(Notes, record.Id)!.Value;
        storage.Update(record.With("title", "second"));
        var second = storage.HeadVersion(Notes, record.Id)!.Value;
        storage.Update(record.With("title", "third"));
        var third = storage.HeadVersion(Notes, record.Id)!.Value;

        var removed = storage.PurgeRecordHistory(Notes, record.Id, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(2, removed);
        Assert.False(storage.Keeps(Notes, record.Id, first));
        Assert.False(storage.Keeps(Notes, record.Id, second));
        Assert.True(storage.Keeps(Notes, record.Id, third));
        Assert.Equal(third, storage.HeadVersion(Notes, record.Id));
        Assert.Equal("third", storage.GetById(Notes, record.Id)!["title"]);
        Assert.Throws<VersionNotKeptException>(() => storage.DiffVersions(Notes, record.Id, first, third));
        Assert.Throws<VersionNotKeptException>(() => storage.RestoreVersion(Notes, record.Id, second));
        Assert.Equal(0, storage.PurgeRecordHistory(Notes, record.Id, DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    /// <summary>An erase leaves nothing of the record: no read of any kind returns it (NF-4d).</summary>
    [Fact]
    public void An_erase_removes_the_record_and_every_version_of_it()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("secret"));
        var first = storage.HeadVersion(Notes, record.Id)!.Value;
        storage.Update(record.With("title", "still secret"));
        var other = storage.Create(Notes, Note("public"));

        Assert.True(storage.Erase(Notes, record.Id));

        Assert.Null(storage.GetById(Notes, record.Id));
        Assert.Null(storage.HeadVersion(Notes, record.Id));
        Assert.False(storage.Keeps(Notes, record.Id, first));
        Assert.Equal([other.Id], storage.GetAll(Notes).Select(static note => note.Id));
        Assert.False(storage.Erase(Notes, record.Id));
        Assert.False(storage.Erase(Notes, Ulid.NewUlid()));
        Assert.Throws<UnknownCollectionException>(() => storage.Erase("nowhere", record.Id));
    }

    /// <summary>History is inside the unit of work like everything else: a failed one leaves it as it was.</summary>
    [Fact]
    public void A_unit_of_work_that_fails_leaves_history_as_it_was()
    {
        var storage = GivenNotes();
        var record = storage.Create(Notes, Note("first"));
        var head = storage.HeadVersion(Notes, record.Id)!.Value;
        Ulid? attempted = null;

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.Update(record.With("title", "second"));
            attempted = storage.HeadVersion(Notes, record.Id);
            throw new InvalidOperationException("abandon");
        }));

        Assert.NotNull(attempted);
        Assert.Equal(head, storage.HeadVersion(Notes, record.Id));
        Assert.False(storage.Keeps(Notes, record.Id, attempted!.Value));
        Assert.Equal("first", storage.GetById(Notes, record.Id)!["title"]);
    }
}
