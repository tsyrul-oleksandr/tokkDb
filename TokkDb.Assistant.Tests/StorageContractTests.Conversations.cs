using TokkDb.Assistant.Storage;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Conversations (SC-10) and the display value (SC-11).
///
/// Both are things four other requirements had been depending on without either being defined.
/// They are here in the shared suite because both implementations have to agree about them: what
/// order conversations come back in, what deleting one does, and what a record is called when
/// nobody said.
/// </summary>
public abstract partial class StorageContractTests
{
    // ---- Conversations (SC-10) ------------------------------------------------------------------

    [Fact]
    public void A_conversation_holds_what_was_said_in_the_order_it_was_said()
    {
        var storage = Storage;
        var conversation = storage.Conversations.Start("Conference expenses");

        storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, "I want to save this",
            ["/tmp/expenses.csv"], RecordIdentity.Next());
        storage.Conversations.Append(conversation.Id, TurnSpeaker.Assistant, "Kept 22 of them.");

        var turns = storage.Conversations.Turns(conversation.Id);

        Assert.Equal(2, turns.Count);
        Assert.Equal(TurnSpeaker.Person, turns[0].Speaker);
        Assert.Equal("I want to save this", turns[0].Text);
        Assert.Equal(["/tmp/expenses.csv"], turns[0].Attachments);
        Assert.True(turns[0].StartedARequest);

        Assert.Equal(TurnSpeaker.Assistant, turns[1].Speaker);
        Assert.False(turns[1].StartedARequest);
        Assert.Empty(turns[1].Attachments);

        Assert.Equal(2, storage.Conversations.Get(conversation.Id)!.TurnCount);
    }

    [Fact]
    public void Conversations_are_listed_with_the_one_used_last_first()
    {
        var storage = Storage;

        var first = storage.Conversations.Start("first");
        var second = storage.Conversations.Start("second");

        storage.Conversations.Append(first.Id, TurnSpeaker.Person, "something else");

        Assert.Equal(
            [first.Id, second.Id],
            storage.Conversations.All().Select(static conversation => conversation.Id));
    }

    [Fact]
    public void A_conversation_can_be_renamed()
    {
        var storage = Storage;
        var conversation = storage.Conversations.Start();

        Assert.False(storage.Conversations.Rename(RecordIdentity.Next(), "nothing"));
        Assert.True(storage.Conversations.Rename(conversation.Id, "Conference expenses"));

        Assert.Equal("Conference expenses", storage.Conversations.Get(conversation.Id)!.Title);
    }

    [Fact]
    public void A_conversation_that_nobody_named_still_has_something_to_be_called()
    {
        var storage = Storage;

        Assert.False(string.IsNullOrWhiteSpace(storage.Conversations.Start().Title));
    }

    /// <summary>
    /// SC-10's acceptance condition, and the distinction worth keeping: a conversation is a
    /// record of what was said, not a container for what was kept. Someone clearing their chat
    /// history has not asked to lose a year of expenses.
    /// </summary>
    [Fact]
    public void Deleting_a_conversation_leaves_the_data_its_requests_stored()
    {
        var storage = GivenExpenses();
        var conversation = storage.Conversations.Start("Conference expenses");

        storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, "save these");
        storage.Create(Expenses, AnExpense());

        Assert.True(storage.Conversations.Delete(conversation.Id));

        Assert.Null(storage.Conversations.Get(conversation.Id));
        Assert.Empty(storage.Conversations.All());
        Assert.Throws<UnknownConversationException>(() => storage.Conversations.Turns(conversation.Id));

        Assert.Single(storage.GetAll(Expenses));
    }

    [Fact]
    public void Appending_to_a_conversation_that_is_not_there_is_refused()
    {
        var storage = Storage;

        Assert.Throws<UnknownConversationException>(() =>
            storage.Conversations.Append(RecordIdentity.Next(), TurnSpeaker.Person, "hello"));
    }

    // ---- The display value (SC-11) ---------------------------------------------------------------

    [Fact]
    public void A_record_reads_as_its_collections_display_rule_says()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        Assert.Equal(
            "EuroPython in Prague",
            DisplayValue.For(storage.GetCollectionDefinition(Expenses)!, record));
    }

    [Fact]
    public void A_collection_with_no_display_rule_is_read_by_its_first_required_text_column()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("note", ColumnType.Text),
            new ColumnDefinition("label", ColumnType.Text, required: true)
        ]));

        var record = storage.Create("readings", new Dictionary<string, object?>
        {
            ["note"] = "written first", ["label"] = "the one that has to be there"
        });

        var definition = storage.GetCollectionDefinition("readings")!;

        Assert.Equal("label", DisplayValue.Column(definition)!.Name);
        Assert.Equal("the one that has to be there", DisplayValue.For(definition, record));
    }

    [Fact]
    public void A_collection_with_no_required_text_column_is_read_by_its_first_text_column()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("value", ColumnType.Integer),
            new ColumnDefinition("note", ColumnType.Text)
        ]));

        var record = storage.Create("readings", new Dictionary<string, object?>
        {
            ["value"] = 3L, ["note"] = "a note"
        });

        Assert.Equal("a note", DisplayValue.For(storage.GetCollectionDefinition("readings")!, record));
    }

    /// <summary>
    /// SC-11's last fallback. A collection with no text in it at all still has to show something
    /// a person can tell one row from another by, and a shortened identity is that - it names the
    /// record without pretending to describe it.
    /// </summary>
    [Fact]
    public void A_collection_with_no_text_at_all_is_read_by_a_shortened_identity()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
            [new ColumnDefinition("value", ColumnType.Integer)]));

        var record = storage.Create("readings", new Dictionary<string, object?> { ["value"] = 3L });
        var definition = storage.GetCollectionDefinition("readings")!;

        Assert.Null(DisplayValue.Column(definition));

        var shown = DisplayValue.For(definition, record);

        Assert.Equal(DisplayValue.ShortenedIdentityLength, shown.Length);
        Assert.EndsWith(shown, record.Id.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_nothing_in_the_column_it_would_be_read_by_falls_back_to_its_identity()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition("readings", columns:
        [
            new ColumnDefinition("note", ColumnType.Text),
            new ColumnDefinition("value", ColumnType.Integer)
        ]));

        var record = storage.Create("readings", new Dictionary<string, object?> { ["value"] = 3L });

        Assert.Equal(
            DisplayValue.Shortened(record.Id),
            DisplayValue.For(storage.GetCollectionDefinition("readings")!, record));
    }

    /// <summary>
    /// SC-11: changing the display rule rewrites no records, which is what makes it <c>Safe</c>
    /// under D-14. The value is derived when it is shown and never stored, so there is nothing
    /// for a change to have to migrate.
    /// </summary>
    [Fact]
    public void Changing_the_display_rule_changes_what_records_read_as_and_rewrites_none_of_them()
    {
        var storage = GivenExpenses();
        var record = storage.Create(Expenses, AnExpense());

        storage.SetDisplayRule(Expenses, new DisplayRule("{event} ({amount_eur})"));

        var definition = storage.GetCollectionDefinition(Expenses)!;

        Assert.Equal("{event} ({amount_eur})", definition.DisplayRule!.Template);
        Assert.Equal("EuroPython (840.50)", DisplayValue.For(definition, storage.GetById(Expenses, record.Id)!));
        Assert.Equal(record, storage.GetById(Expenses, record.Id));

        storage.SetDisplayRule(Expenses, null);
        Assert.Null(storage.GetCollectionDefinition(Expenses)!.DisplayRule);
    }

    [Fact]
    public void A_display_rule_naming_a_column_the_collection_does_not_have_is_refused()
    {
        var storage = GivenExpenses();

        Assert.Throws<InvalidDefinitionException>(() =>
            storage.SetDisplayRule(Expenses, new DisplayRule("{venue}")));
    }

    [Fact]
    public void What_the_application_remembers_about_a_collection_can_be_changed()
    {
        var storage = GivenExpenses();

        storage.SetMetadata(Expenses, new Dictionary<string, string?>
        {
            ["source"] = "expenses-2026.csv", ["nothing"] = null
        });

        var metadata = storage.GetCollectionDefinition(Expenses)!.Metadata;

        Assert.Equal("expenses-2026.csv", metadata["source"]);
        Assert.Null(metadata["nothing"]);
    }
}
