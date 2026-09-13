using TokkDb.Assistant.Storage;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Relations, and what a delete does to the records that refer to the record being deleted
/// (SC-8, SC-8a).
///
/// Shared, because this is exactly the kind of question §2.2 of the engine plan found two
/// backends answering differently: what "restrict" means, whether a cascade reports what it
/// took, whether a rule that cannot hold is refused when it is written or when it first bites.
/// </summary>
public abstract partial class StorageContractTests
{
    private const string Conferences = "conferences";

    /// <summary>
    /// The two things of the acceptance criterion: conferences, recognised by a name no two of
    /// them share, and expenses that refer to one.
    /// </summary>
    private IStorage GivenConferencesAndExpenses(
        RelationIntegrity integrity = RelationIntegrity.Restrict,
        bool conferenceRequired = false)
    {
        var storage = Storage;

        storage.CreateCollection(new CollectionDefinition(Conferences, "conferences I went to", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, "what it was called", required: true, unique: true),
            new ColumnDefinition("city", ColumnType.Text, "where it was")
        ]));

        storage.CreateCollection(new CollectionDefinition(Expenses, "money I spent", columns:
        [
            new ColumnDefinition("what", ColumnType.Text, "what the money was for", required: true),
            new ColumnDefinition("conference", ColumnType.Text, "the conference it was for",
                required: conferenceRequired),
            new ColumnDefinition("amount_eur", ColumnType.Decimal, "what it came to")
        ]));

        storage.AddRelation(new RelationDefinition(
            "expense_conference", Expenses, "conference", Conferences, "name", integrity,
            "the conference this expense was for"));

        return storage;
    }

    private static Ulid AConference(IStorage storage, string name = "EuroPython")
    {
        return storage.Create(Conferences, new Dictionary<string, object?>
        {
            ["name"] = name, ["city"] = "Prague"
        }).Id;
    }

    private static void SomeExpenses(IStorage storage, int howMany, string conference = "EuroPython")
    {
        foreach (var i in Enumerable.Range(0, howMany))
        {
            storage.Create(Expenses, new Dictionary<string, object?>
            {
                ["what"] = $"thing {i}", ["conference"] = conference, ["amount_eur"] = 10m + i
            });
        }
    }

    [Fact]
    public void A_relation_survives_being_written_and_read_back()
    {
        var storage = GivenConferencesAndExpenses(RelationIntegrity.Cascade);

        var relation = Assert.Single(storage.GetRelations());

        Assert.Equal("expense_conference", relation.Name);
        Assert.Equal(Expenses, relation.FromCollection);
        Assert.Equal("conference", relation.FromColumn);
        Assert.Equal(Conferences, relation.ToCollection);
        Assert.Equal("name", relation.ToColumn);
        Assert.Equal(RelationIntegrity.Cascade, relation.Integrity);
        Assert.Equal("the conference this expense was for", relation.Purpose);
    }

    [Fact]
    public void A_relation_can_be_removed_and_the_records_are_untouched()
    {
        var storage = GivenConferencesAndExpenses();
        var conference = AConference(storage);
        SomeExpenses(storage, 2);

        Assert.True(storage.RemoveRelation("expense_conference"));
        Assert.Empty(storage.GetRelations());

        // The constraint is gone; nothing it constrained is.
        Assert.True(storage.Delete(Conferences, conference).Removed);
        Assert.Equal(2, storage.GetAll(Expenses).Count);
    }

    [Fact]
    public void A_relation_to_a_column_two_records_could_share_is_refused()
    {
        var storage = Storage;

        storage.CreateCollection(new CollectionDefinition(Conferences, columns:
            [new ColumnDefinition("name", ColumnType.Text, required: true)]));
        storage.CreateCollection(new CollectionDefinition(Expenses, columns:
            [new ColumnDefinition("conference", ColumnType.Text)]));

        var thrown = Assert.Throws<InvalidDefinitionException>(() => storage.AddRelation(
            new RelationDefinition("expense_conference", Expenses, "conference", Conferences, "name")));

        Assert.Equal("relation", thrown.Member);
    }

    /// <summary>
    /// SC-8's third acceptance condition. Set empty and a required column cannot both hold, and
    /// the delete is the wrong moment to find that out - so it is refused when the relation is
    /// created, while somebody is still looking at the decision.
    /// </summary>
    [Fact]
    public void Emptying_a_column_that_has_to_have_a_value_is_refused_when_the_relation_is_created()
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(() =>
            GivenConferencesAndExpenses(RelationIntegrity.SetEmpty, conferenceRequired: true));

        Assert.Equal("relation", thrown.Member);
    }

    /// <summary>
    /// SC-8's first acceptance condition, in full: refused by default, naming the four expenses
    /// in plain words rather than as an integrity error.
    /// </summary>
    [Fact]
    public void Deleting_a_conference_four_expenses_point_at_is_refused_and_names_the_four()
    {
        var storage = GivenConferencesAndExpenses();
        var conference = AConference(storage);
        SomeExpenses(storage, 4);

        var effect = storage.InspectDelete(Conferences, conference);

        Assert.True(effect.RecordExists);
        Assert.True(effect.IsRefused);
        Assert.Equal(4, effect.Blocking.Count);
        Assert.All(effect.Blocking, reference => Assert.Equal(Expenses, reference.CollectionName));

        var thrown = Assert.Throws<IntegrityRefusedException>(() => storage.Delete(Conferences, conference));

        Assert.Equal(4, thrown.Referring.Count);
        Assert.Equal("expense_conference", thrown.Relation.Name);
        Assert.Equal(conference, thrown.Target.Id);
        Assert.Contains("4 records", thrown.Message, StringComparison.Ordinal);

        // Nothing went.
        Assert.NotNull(storage.GetById(Conferences, conference));
        Assert.Equal(4, storage.GetAll(Expenses).Count);
    }

    [Fact]
    public void Deleting_a_conference_nothing_points_at_is_not_refused()
    {
        var storage = GivenConferencesAndExpenses();
        var conference = AConference(storage);

        AConference(storage, "Something else");
        SomeExpenses(storage, 2, conference: "Something else");

        var result = storage.Delete(Conferences, conference);

        Assert.True(result.Removed);
        Assert.Empty(result.AlsoRemoved);
        Assert.Equal(2, storage.GetAll(Expenses).Count);
    }

    /// <summary>
    /// SC-8a. Cascade takes the four with it, and each of them comes back named - which is what
    /// lets the caller write a change record per deletion, and what makes undoing the request
    /// able to restore all five.
    /// </summary>
    [Fact]
    public void Cascade_takes_the_expenses_with_it_and_names_every_one()
    {
        var storage = GivenConferencesAndExpenses(RelationIntegrity.Cascade);
        var conference = AConference(storage);
        SomeExpenses(storage, 4);

        var effect = storage.InspectDelete(Conferences, conference);

        Assert.False(effect.IsRefused);
        Assert.Equal(4, effect.WouldAlsoBeRemoved.Count);

        var result = storage.Delete(Conferences, conference);

        Assert.True(result.Removed);
        Assert.Equal(4, result.AlsoRemoved.Count);
        Assert.Equal(5, result.TotalRemoved);
        Assert.Empty(storage.GetAll(Expenses));
        Assert.Null(storage.GetById(Conferences, conference));
    }

    [Fact]
    public void Set_empty_leaves_the_expenses_and_empties_what_they_referred_to()
    {
        var storage = GivenConferencesAndExpenses(RelationIntegrity.SetEmpty);
        var conference = AConference(storage);
        SomeExpenses(storage, 3);

        var result = storage.Delete(Conferences, conference);

        Assert.True(result.Removed);
        Assert.Equal(3, result.Cleared.Count);
        Assert.Empty(result.AlsoRemoved);

        var expenses = storage.GetAll(Expenses);
        Assert.Equal(3, expenses.Count);
        Assert.All(expenses, expense => Assert.Null(expense["conference"]));
    }

    /// <summary>
    /// The engine refuses a reference with no target on the way in, and the contract says the
    /// same thing: a relation is a rule about writes as well as about deletes.
    /// </summary>
    [Fact]
    public void An_expense_can_only_refer_to_a_conference_that_is_there()
    {
        var storage = GivenConferencesAndExpenses();
        AConference(storage);

        Assert.Throws<StorageValidationException>(() => storage.Create(Expenses, new Dictionary<string, object?>
        {
            ["what"] = "a taxi", ["conference"] = "a conference nobody went to", ["amount_eur"] = 30m
        }));

        Assert.Empty(storage.GetAll(Expenses));
    }

    /// <summary>
    /// SC-7's relation traversal: what has to be true of the conference becomes a condition on
    /// the expenses, without the caller writing a join the engine has no operator for.
    /// </summary>
    [Fact]
    public void A_query_can_ask_about_the_thing_on_the_other_side_of_a_relation()
    {
        var storage = GivenConferencesAndExpenses();

        AConference(storage);
        storage.Create(Conferences, new Dictionary<string, object?> { ["name"] = "PyCon", ["city"] = "Lviv" });

        SomeExpenses(storage, 2);
        SomeExpenses(storage, 3, conference: "PyCon");

        var lviv = storage.ExecuteQuery(new StorageQuery(Expenses).Through(new QueryTraversal(
            "expense_conference",
            new StorageQuery(Conferences, [new QueryCondition("city", QueryOperator.Equals, "Lviv")]))));

        Assert.Equal(3, lviv.Records.Count);
        Assert.All(lviv.Records, record => Assert.Equal("PyCon", record["conference"]));
    }

    [Fact]
    public void A_traversal_that_matches_nothing_on_the_other_side_returns_nothing()
    {
        var storage = GivenConferencesAndExpenses();
        AConference(storage);
        SomeExpenses(storage, 2);

        var result = storage.ExecuteQuery(new StorageQuery(Expenses).Through(new QueryTraversal(
            "expense_conference",
            new StorageQuery(Conferences, [new QueryCondition("city", QueryOperator.Equals, "Reykjavik")]))));

        Assert.Empty(result.Records);
    }

    [Fact]
    public void A_traversal_through_a_relation_that_is_not_there_is_refused()
    {
        var storage = GivenConferencesAndExpenses();

        Assert.Throws<UnknownRelationException>(() => storage.ExecuteQuery(
            new StorageQuery(Expenses).Through(new QueryTraversal(
                "no_such_relation", new StorageQuery(Conferences)))));
    }
}
