using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Safe, review, destructive (D-14, AG-4, AG-11c, AG-11d, UI-4, step 4.5): every change is
/// classified by what it can do, its evidence computed before the question is put, and the card
/// states the loss in counts and examples - and says when a change cannot be undone.
/// </summary>
public sealed class ClassificationTests
{
    private static (MemoryStorage Storage, ChangeClassifier Classifier) Given(int records = 14)
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("conferences", "conferences I went to", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, required: true),
            new ColumnDefinition("city", ColumnType.Text),
            new ColumnDefinition("cost", ColumnType.Text),
            new ColumnDefinition("notes", ColumnType.Text)
        ]));

        for (var i = 0; i < records; i++)
        {
            storage.Create("conferences", new Dictionary<string, object?>
            {
                ["name"] = $"Conf {i}",
                ["city"] = i % 2 == 0 ? "Lviv" : "Kyiv",
                ["cost"] = i is 3 or 5 or 7 ? "unknown" : (100 * i).ToString(),
                ["notes"] = i < 3 ? null : $"note {i}"
            });
        }

        return (storage, new ChangeClassifier(storage));
    }

    /// <summary>Adding an optional field prompts nothing.</summary>
    [Fact]
    public void Adding_an_optional_field_is_safe()
    {
        var (_, classifier) = Given();
        var change = classifier.Classify(new AddField("conferences", new ColumnDefinition("venue", ColumnType.Text)));

        Assert.Equal(ChangeClass.Safe, change.Class);
        Assert.False(change.NeedsConfirmation);
        Assert.Equal(Reversibility.Reversible, change.Reversibility);
    }

    /// <summary>Adding a required field reports how many records become incomplete.</summary>
    [Fact]
    public void Adding_a_required_field_reports_how_many_records_become_incomplete()
    {
        var (_, classifier) = Given();
        var change = classifier.Classify(new AddField("conferences", new ColumnDefinition("venue", ColumnType.Text, required: true)));

        Assert.Equal(ChangeClass.ReviewRequired, change.Class);
        Assert.Equal(14, change.Evidence.Count);
        Assert.Contains("all 14 conferences you have would be left without a venue", change.Evidence.Sentence);
        Assert.Equal(3, change.Evidence.Examples.Count);
    }

    /// <summary>Adding a unique rule reports how many existing values collide.</summary>
    [Fact]
    public void Making_a_field_unique_reports_the_collisions()
    {
        var (_, classifier) = Given();
        var change = classifier.Classify(new MakeUnique("conferences", "city"));

        Assert.Equal(ChangeClass.ReviewRequired, change.Class);
        Assert.Equal(14, change.Evidence.Count);
        Assert.Contains("share a city with another", change.Evidence.Sentence);
        Assert.Contains(change.Evidence.Examples, static example => example.Contains(" and ", StringComparison.Ordinal));

        var fine = classifier.Classify(new MakeUnique("conferences", "name"));
        Assert.Equal(0, fine.Evidence.Count);
        Assert.Contains("no two of your 14 conferences share a name today", fine.Evidence.Sentence);
    }

    /// <summary>Adding a relation reports how many source values match nothing.</summary>
    [Fact]
    public void Adding_a_relation_reports_the_values_that_match_nothing()
    {
        var (storage, classifier) = Given();
        storage.CreateCollection(new CollectionDefinition("cities", "cities", columns: [new ColumnDefinition("name", ColumnType.Text, unique: true)]));
        storage.Create("cities", new Dictionary<string, object?> { ["name"] = "Lviv" });

        var change = classifier.Classify(new AddRelation(new RelationDefinition("conference_city", "conferences", "city", "cities", "name")));

        Assert.Equal(ChangeClass.ReviewRequired, change.Class);
        Assert.Equal(7, change.Evidence.Count);
        Assert.Equal(14, change.Evidence.OutOf);
        Assert.Contains("7 of your 14 conferences name a city that matches none of cities", change.Evidence.Sentence);
    }

    /// <summary>Removing a field reports how many records hold a value, with three examples, and is destructive.</summary>
    [Fact]
    public void Removing_a_field_reports_how_many_records_hold_a_value()
    {
        var (_, classifier) = Given();
        var change = classifier.Classify(new RemoveField("conferences", "notes"));

        Assert.Equal(ChangeClass.Destructive, change.Class);
        Assert.Equal(11, change.Evidence.Count);
        Assert.Equal(14, change.Evidence.OutOf);
        Assert.Equal(3, change.Evidence.Examples.Count);
        Assert.Contains("11 of your 14 conferences have a notes, and those values would go", change.Evidence.Sentence);
        Assert.Equal(Reversibility.ReversibleWithConditions, change.Reversibility);
    }

    /// <summary>AG-11c, AG-11d: removing a field whose values exceed the journal's cap is NotReversible before it runs, and the card says so.</summary>
    [Fact]
    public void Removing_a_field_past_the_journals_cap_is_not_reversible_before_it_runs()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("wide", "wide things", columns: [new ColumnDefinition("body", ColumnType.Text)]));
        for (var i = 0; i < 200; i++) storage.Create("wide", new Dictionary<string, object?> { ["body"] = new string('x', 900) });

        var change = new ChangeClassifier(storage).Classify(new RemoveField("wide", "body"));
        Assert.Equal(Reversibility.NotReversible, change.Reversibility);

        var card = ConfirmationCard.For([change], TimeSpan.FromDays(90));
        Assert.False(card.CanBeUndone);
        Assert.Equal(ConfirmationCard.CannotBeUndone, card.UndoNote);
    }

    /// <summary>A widening retype is safe; a narrowing one reports the values that will not convert.</summary>
    [Fact]
    public void A_retype_is_safe_when_it_widens_and_destructive_when_it_loses()
    {
        var (_, classifier) = Given();

        var lossy = classifier.Classify(new RetypeField("conferences", "cost", ColumnType.Decimal));
        Assert.Equal(ChangeClass.Destructive, lossy.Class);
        Assert.Equal(3, lossy.Evidence.Count);
        Assert.Contains("3 of the 14 values of cost cannot be read as number", lossy.Evidence.Sentence);
        Assert.NotEqual(Reversibility.Reversible, lossy.Reversibility);

        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("n", columns: [new ColumnDefinition("count", ColumnType.Integer)]));
        var widening = new ChangeClassifier(storage).Classify(new RetypeField("n", "count", ColumnType.Decimal));
        Assert.Equal(ChangeClass.Safe, widening.Class);
    }

    /// <summary>Deleting records states the window on the card (NF-4d) and what goes with them (SC-8a).</summary>
    [Fact]
    public void Deleting_records_states_the_window_and_what_goes_with_them()
    {
        var (storage, classifier) = Given(3);
        var ids = storage.GetAll("conferences").Select(static record => record.Id).Take(2).ToList();

        var change = classifier.Classify(new DeleteRecords("conferences", ids));
        var card = ConfirmationCard.For([change], TimeSpan.FromDays(90));

        Assert.Equal(ChangeClass.Destructive, change.Class);
        Assert.Equal(2, change.Evidence.Count);
        Assert.True(card.CanBeUndone);
        Assert.Contains("90 days", card.UndoNote);
        Assert.Equal(2, card.Examples.Count);
    }

    /// <summary>Removing a whole thing cannot be undone, and the card says so before it runs.</summary>
    [Fact]
    public void Removing_a_thing_is_not_reversible_and_the_card_says_so()
    {
        var (_, classifier) = Given();
        var change = classifier.Classify(new RemoveThing("conferences"));
        var card = ConfirmationCard.For([change], TimeSpan.FromDays(90));

        Assert.Equal(Reversibility.NotReversible, change.Reversibility);
        Assert.Equal(ConfirmationCard.CannotBeUndone, card.UndoNote);
        Assert.Contains("all 14 conferences would go", card.Lines[0]);
    }

    /// <summary>UI-4, UI-2: the card reads as what will be lost and how much, in no schema term.</summary>
    [Fact]
    public void The_card_is_in_plain_words()
    {
        var (_, classifier) = Given();
        var card = ConfirmationCard.For([classifier.Classify(new RemoveField("conferences", "notes"))], TimeSpan.FromDays(90));

        var text = (card.Title + " " + string.Join(" ", card.Lines) + " " + card.UndoNote + " " + card.Yes + " " + card.No).ToLowerInvariant();
        foreach (var word in new[] { "column", "schema", "index", "query", "constraint", "nullable", "collection", "table" })
        {
            Assert.DoesNotContain(word, text);
        }

        Assert.Equal("Drop the notes from conferences?", card.Title);
        Assert.Contains("11 of your 14 conferences", card.Lines[0]);
    }
}
