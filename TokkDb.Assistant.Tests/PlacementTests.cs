using System.Reflection;
using System.Runtime.CompilerServices;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The placement proposal (AG-3, AG-3a, AG-3b, AG-3c, AG-3d, step 4.4): one object, confidence
/// computed in C# and never read from the model, a floor and a gap, a shortlist the model cannot
/// step outside, and a validated proposal nothing can change.
/// </summary>
public sealed class PlacementTests
{
    private static (MemoryStorage Storage, Placer Placer) Given()
    {
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("Conference", "conferences attended", columns:
        [
            new ColumnDefinition("Name", ColumnType.Text, required: true),
            new ColumnDefinition("City", ColumnType.Text),
            new ColumnDefinition("Cost", ColumnType.Decimal),
            new ColumnDefinition("Paid_on", ColumnType.Date)
        ]));
        return (storage, new Placer(storage, new ChangeClassifier(storage)));
    }

    private static IncomingShape Shape(params (string Name, ColumnType Kind)[] fields) =>
        new("conferences", null,
            [.. fields.Select(field => new IncomingField(field.Name, field.Kind, field.Kind, ["x"], 1, 0, []))],
            [new IncomingRow(fields.ToDictionary(field => field.Name, field => (object?)(field.Kind is ColumnType.Decimal ? 1m : field.Kind is ColumnType.Date ? new DateOnly(2025, 1, 1) : "x")), 2)],
            "conferences.xlsx");

    /// <summary>AG-3: a cost column called amount_eur maps onto Cost rather than adding a second field, even when the model left it out.</summary>
    [Fact]
    public void Amount_eur_maps_onto_Cost_rather_than_adding_a_field()
    {
        var (storage, placer) = Given();
        var shape = Shape(("event", ColumnType.Text), ("city", ColumnType.Text), ("amount_eur", ColumnType.Decimal), ("paid_on", ColumnType.Date));
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, null);

        var proposal = placer.Propose(shape, shortlist, new MappingAnswer("Conference", null, null, [("event", "Name"), ("city", "City"), ("amount_eur", ""), ("paid_on", "Paid_on")]));

        Assert.False(proposal.IsNew);
        Assert.Equal("Conference", proposal.Target);
        Assert.Empty(proposal.NewFields);
        var cost = Assert.Single(proposal.Mappings, static mapping => mapping.Incoming == "amount_eur");
        Assert.Equal("Cost", cost.Existing);
        Assert.Equal(MappingKind.Renamed, cost.Kind);
        Assert.Empty(proposal.UnfilledExisting);
        Assert.True(proposal.Confidence.Score >= 0.9, proposal.Confidence.Score.ToString());
        Assert.Contains(proposal.Confidence.Evidence, static line => line.StartsWith("4 of 4 incoming fields", StringComparison.Ordinal));
    }

    /// <summary>AG-3: a spreadsheet of expenses proposes a new thing, as one object validation, the card and the trace all read.</summary>
    [Fact]
    public void A_spreadsheet_of_expenses_proposes_a_new_thing()
    {
        var (storage, placer) = Given();
        var shape = Shape(("what", ColumnType.Text), ("amount", ColumnType.Decimal), ("paid_on", ColumnType.Date), ("receipt", ColumnType.Text)) with { Name = "expenses" };
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, "here are my expenses");

        var proposal = placer.Propose(shape, shortlist, new MappingAnswer("none", "expenses", "money spent", []));

        Assert.True(proposal.IsNew);
        Assert.Equal("expenses", proposal.Target);
        Assert.Equal("money spent", proposal.Purpose);
        Assert.Equal(4, proposal.NewFields.Count);
        Assert.Contains(proposal.Actions, static action => action.Action is CreateThing);
        Assert.Contains(proposal.Actions, static action => action.Action is StoreRecords { Count: 1 });
        Assert.All(proposal.Actions, static action => Assert.Equal(ChangeClass.Safe, action.Class));
        Assert.Equal("Conference", proposal.Confidence.RunnerUpName);
        Assert.Contains("expenses", proposal.Describe());
    }

    /// <summary>AG-3a: a model that reports a confidence of its own has it ignored; the spike's own model said 0 on a correct answer.</summary>
    [Fact]
    public void The_models_own_confidence_is_not_what_the_gate_reads()
    {
        var (storage, placer) = Given();
        var shape = Shape(("Name", ColumnType.Text), ("City", ColumnType.Text), ("Cost", ColumnType.Decimal), ("Paid_on", ColumnType.Date));
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, null);

        var parsed = Answers.Mapping(shortlist)("""{"choice":"Conference","newName":"","purpose":"","fields":[],"confidence":0}""");
        Assert.True(parsed.IsValid);

        var proposal = placer.Propose(shape, shortlist, parsed.Value!);

        Assert.True(proposal.Confidence.Score >= 0.9);
        Assert.Equal(PlacementDecision.ApplySilently, PlacementDecisions.Decide(proposal, PlacementThresholds.Default));
    }

    /// <summary>AG-3c: the placement call returns a choice from the shortlist or "none"; a name that was not offered is refused and sent back.</summary>
    [Fact]
    public void A_name_that_was_not_offered_cannot_be_chosen()
    {
        var (storage, _) = Given();
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), ["Name", "Cost"], null);

        var refused = Answers.Mapping(shortlist)("""{"choice":"Expenses","newName":"","purpose":"","fields":[]}""");
        Assert.False(refused.IsValid);
        Assert.Contains("was not offered", refused.Problem);

        Assert.True(Answers.Mapping(shortlist)("""{"choice":"none","newName":"expenses","purpose":"","fields":[]}""").IsValid);
        Assert.True(Answers.Mapping(shortlist)("""{"choice":"Conference","newName":"","purpose":"","fields":[]}""").IsValid);
    }

    /// <summary>Step 0.4's finding: "new" with the very name that was offered is read as the choice of that thing.</summary>
    [Fact]
    public void A_new_answer_naming_an_offered_thing_is_read_as_the_choice()
    {
        var (storage, placer) = Given();
        var shape = Shape(("Name", ColumnType.Text), ("City", ColumnType.Text), ("Cost", ColumnType.Decimal), ("Paid_on", ColumnType.Date));
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, null);

        var proposal = placer.Propose(shape, shortlist, new MappingAnswer("none", "conference", "conferences", []));

        Assert.False(proposal.IsNew);
        Assert.Equal("Conference", proposal.Target);
    }

    /// <summary>AG-3b: two candidates within the gap produce a question even when both score highly; one alone above the floor applies silently; a new thing has a higher bar.</summary>
    [Fact]
    public void The_floor_and_the_gap_decide()
    {
        var thresholds = PlacementThresholds.Default;
        var existing = new PlacementProposal("a", false, null, [], [], [], [], new Confidence(0.9, [], 0.85, "b"), "", null, "s", []);

        Assert.Equal(PlacementDecision.Ask, PlacementDecisions.Decide(existing, thresholds));
        Assert.Contains("could belong to a or to b", PlacementDecisions.WhyAsked(existing, thresholds));
        Assert.Equal(PlacementDecision.ApplySilently, PlacementDecisions.Decide(existing with { Confidence = new Confidence(0.9, [], 0.5, "b") }, thresholds));
        Assert.Equal(PlacementDecision.ApplySilently, PlacementDecisions.Decide(existing with { Confidence = new Confidence(0.7, [], null, null) }, thresholds));
        Assert.Equal(PlacementDecision.Ask, PlacementDecisions.Decide(existing with { Confidence = new Confidence(0.55, [], null, null) }, thresholds));

        var fresh = existing with { IsNew = true, Confidence = new Confidence(0.7, [], null, null) };
        Assert.Equal(PlacementDecision.Ask, PlacementDecisions.Decide(fresh, thresholds));
        Assert.Equal(PlacementDecision.ApplySilently, PlacementDecisions.Decide(fresh with { Confidence = new Confidence(0.8, [], null, null) }, thresholds));
        Assert.True(thresholds.NewFloor > thresholds.Floor);
    }

    /// <summary>AG-3d: a validated proposal exposes no mutable member, and carries a content hash over the proposal and the rows.</summary>
    [Fact]
    public void A_validated_proposal_is_immutable_and_hashed()
    {
        foreach (var type in new[] { typeof(ValidatedProposal), typeof(PlacementProposal), typeof(FieldMapping), typeof(Confidence), typeof(ProposedField) })
        {
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = property.SetMethod;
                if (setter is null || !setter.IsPublic) continue;

                Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
            }
        }

        var (storage, placer) = Given();
        var shape = Shape(("Name", ColumnType.Text), ("City", ColumnType.Text), ("Cost", ColumnType.Decimal), ("Paid_on", ColumnType.Date));
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, null);
        var proposal = placer.Propose(shape, shortlist, new MappingAnswer("Conference", null, null, []));
        var rows = Placer.Rows(shape, proposal);

        var validated = ValidatedProposal.Validate(storage, proposal, rows, MergePolicy.SkipExisting);
        var again = ValidatedProposal.Validate(storage, proposal, rows, MergePolicy.SkipExisting);
        var otherRows = ValidatedProposal.Validate(storage, proposal, [new ImportRow(new Dictionary<string, object?> { ["Name"] = "y" }, 3)], MergePolicy.SkipExisting);

        Assert.Equal(validated.Hash, again.Hash);
        Assert.NotEqual(validated.Hash, otherRows.Hash);
        Assert.NotEqual(validated.Hash, ValidatedProposal.Validate(storage, proposal, rows, MergePolicy.AddAll).Hash);

        // Validation refuses what does not fit, naming it.
        var wrong = proposal with { Mappings = [new FieldMapping("Name", ColumnType.Text, "Title", ColumnType.Text, MappingKind.Exact)] };
        var refused = Assert.Throws<InvalidProposalException>(() => ValidatedProposal.Validate(storage, wrong, rows, MergePolicy.SkipExisting));
        Assert.Contains(refused.Problems, static problem => problem.Contains("no field called 'Title'", StringComparison.Ordinal));
    }

    /// <summary>The rows arrive by the target's names, typed as the target keeps them, with a row that will not read reported by line (IN-4).</summary>
    [Fact]
    public void Rows_are_mapped_to_the_targets_names_and_types()
    {
        var (storage, placer) = Given();
        var shape = new IncomingShape("conferences", null,
            [new IncomingField("event", ColumnType.Text, ColumnType.Text, [], 2, 0, []), new IncomingField("amount_eur", ColumnType.Text, ColumnType.Decimal, [], 2, 0, [3])],
            [
                new IncomingRow(new Dictionary<string, object?> { ["event"] = "EuroPython", ["amount_eur"] = "840.50" }, 2),
                new IncomingRow(new Dictionary<string, object?> { ["event"] = "DevDays", ["amount_eur"] = "about 600" }, 3)
            ], "x.csv");
        var shortlist = CollectionPrefilter.Shortlist(storage.GetCollectionDefinitions(), shape.FieldNames, null);
        var proposal = placer.Propose(shape, shortlist, new MappingAnswer("Conference", null, null, []));

        var rows = Placer.Rows(shape, proposal);

        Assert.Equal(840.50m, rows[0].Fields["Cost"]);
        Assert.Equal("EuroPython", rows[0].Fields["Name"]);
        Assert.Null(rows[0].Unreadable);
        Assert.Equal(3, rows[1].LineNumber);
        Assert.Contains("is not number", rows[1].Unreadable);
    }
}
