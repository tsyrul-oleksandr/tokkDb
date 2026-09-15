using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Context assembly (§6.1, AG-6, AG-6a, step 4.3): the digest is bounded, the conversation is
/// bounded and keeps what was said to remember, and the prefix is byte-identical between calls.
/// </summary>
public sealed class ContextAssemblyTests
{
    private static CollectionDefinition Thing(int index) =>
        new($"thing_{index:000}", $"the {index}th kind of thing a person keeps, described at some length so that the digest has to choose", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, required: true),
            new ColumnDefinition("cost", ColumnType.Decimal),
            new ColumnDefinition("paid_on", ColumnType.Date),
            new ColumnDefinition("notes", ColumnType.Text)
        ]);

    /// <summary>The digest of a 200-collection storage is bounded by the budget, not by the storage.</summary>
    [Fact]
    public void The_digest_of_a_200_collection_storage_is_bounded()
    {
        var definitions = Enumerable.Range(1, 200).Select(Thing).ToList();

        var digest = SchemaDigest.Render(definitions, [], budgetTokens: 1_200);

        Assert.True(Tokens.Estimate(digest) <= 1_200, $"the digest is {Tokens.Estimate(digest)} tokens");
        Assert.Contains("... and ", digest);
        Assert.Contains("thing_001", digest);

        // Most recently changed first, when not all of them fit.
        var recent = SchemaDigest.Render(definitions, [], 400, definition => definition.Name == "thing_150" ? DateTimeOffset.UtcNow : null);
        Assert.StartsWith("things stored:\n- thing_150", recent);

        // And a small storage renders whole, with its relations.
        var small = SchemaDigest.Render(definitions.Take(2).ToList(),
            [new RelationDefinition("r", "thing_002", "name", "thing_001", "name", purpose: "the thing it belongs to")], 1_200);
        Assert.Contains("thing_002.name refers to thing_001.name (the thing it belongs to)", small);
        Assert.DoesNotContain("... and", small);
    }

    /// <summary>The kinds in the digest are the words the model is asked to use, both ways.</summary>
    [Fact]
    public void The_digest_speaks_the_kinds_the_operations_ask_for()
    {
        foreach (var type in Enum.GetValues<ColumnType>())
        {
            Assert.Equal(type, SchemaDigest.Type(SchemaDigest.Kind(type)));
            Assert.Contains($"\"{SchemaDigest.Kind(type)}\"", Operations.KindWords);
        }

        Assert.Null(SchemaDigest.Type("integer"));
    }

    /// <summary>AG-6: a 200-turn conversation stays within budget, and turn 3's constraint is still there at turn 150.</summary>
    [Fact]
    public void A_200_turn_conversation_stays_within_budget_and_keeps_a_constraint_from_turn_3()
    {
        var conversation = Ulid.NewUlid();
        var turns = new List<ConversationTurn>();

        for (var i = 0; i < 200; i++)
        {
            var text = i == 2
                ? "Always keep the amounts in euros, whatever the file says."
                : i % 2 == 0 ? $"here is message number {i} with a fair amount of text in it to fill the window" : $"done with {i}";

            turns.Add(new ConversationTurn(Ulid.NewUlid(), conversation, i % 2 == 0 ? TurnSpeaker.Person : TurnSpeaker.Assistant, text, [], null, DateTimeOffset.UtcNow.AddMinutes(i)));
        }

        var atTurn150 = ConversationWindow.Render(turns.Take(150).ToList(), budgetTokens: 400);
        var whole = ConversationWindow.Render(turns, budgetTokens: 400);

        Assert.True(Tokens.Estimate(atTurn150) <= 400, $"{Tokens.Estimate(atTurn150)} tokens");
        Assert.True(Tokens.Estimate(whole) <= 400, $"{Tokens.Estimate(whole)} tokens");
        Assert.Contains("Always keep the amounts in euros", atTurn150);
        Assert.Contains("Always keep the amounts in euros", whole);
        Assert.Contains("message number 148", atTurn150);
        Assert.DoesNotContain("message number 100", atTurn150);
    }

    /// <summary>AG-6a: two calls of one operation with different user input carry the same prefix, byte for byte.</summary>
    [Fact]
    public async Task The_prefix_of_two_calls_with_different_input_is_byte_identical()
    {
        using var host = new AgentsHost();
        host.Model.Always(Operations.Query.Name, "{}");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var digest = SchemaDigest.Render([Thing(1), Thing(2)], [], 1_000);
        var prefix = PromptPrefix.Assemble(Operations.Query, digest, host.Tools);

        await host.Runner.RunAsync(Operations.Query, new AssembledContext(prefix, "how much did I spend", EgressClass.SchemaDigest), Ok, request.Id);
        await host.Runner.RunAsync(Operations.Query, new AssembledContext(prefix, "which were in Lviv", EgressClass.SchemaDigest), Ok, request.Id);

        var first = host.Model.Requests[0];
        var second = host.Model.Requests[1];

        Assert.Equal(first.Prefix, second.Prefix);
        Assert.Equal(PromptPrefix.Hash(first.Prefix), PromptPrefix.Hash(second.Prefix));
        Assert.NotEqual(first.Content, second.Content);
        Assert.StartsWith(Operations.Query.Instructions.Trim(), first.Prefix);
        Assert.EndsWith("tools: none", first.Prefix);
    }

    /// <summary>The assertion covers ordering, whitespace and the tool block: any of them moving changes the hash.</summary>
    [Fact]
    public void The_prefix_hash_changes_when_the_order_or_the_whitespace_differs()
    {
        var tools = new ToolCatalog();
        var digest = SchemaDigest.Render([Thing(1)], [], 1_000);

        var assembled = PromptPrefix.Assemble(Operations.Query, digest, tools);
        var reordered = digest.Trim() + PromptPrefix.Separator + Operations.Query.Instructions.Trim() + PromptPrefix.Separator + tools.Describe(Operations.Query);
        var respaced = assembled.Replace("\n\n", "\n \n");
        var withTool = PromptPrefix.Assemble(Operations.Query with { Tools = ["count_records"] }, digest, new ToolCatalog(new MemoryStorage()));

        Assert.NotEqual(PromptPrefix.Hash(assembled), PromptPrefix.Hash(reordered));
        Assert.NotEqual(PromptPrefix.Hash(assembled), PromptPrefix.Hash(respaced));
        Assert.NotEqual(PromptPrefix.Hash(assembled), PromptPrefix.Hash(withTool));
        Assert.Equal(PromptPrefix.Hash(assembled), PromptPrefix.Hash(PromptPrefix.Assemble(Operations.Query, digest, tools)));
    }

    /// <summary>The pre-filter ranks by the incoming shape first and the words second, with synonyms for the everyday fields.</summary>
    [Fact]
    public void The_prefilter_chooses_the_plausible_things()
    {
        var conferences = new CollectionDefinition("conferences", "conferences I went to", columns:
        [
            new ColumnDefinition("event", ColumnType.Text, required: true),
            new ColumnDefinition("city", ColumnType.Text),
            new ColumnDefinition("Cost", ColumnType.Decimal),
            new ColumnDefinition("paid_on", ColumnType.Date)
        ]);
        var trips = new CollectionDefinition("trips", "journeys and where they went", columns:
        [
            new ColumnDefinition("destination", ColumnType.Text),
            new ColumnDefinition("price", ColumnType.Decimal),
            new ColumnDefinition("left_on", ColumnType.Date)
        ]);
        var books = new CollectionDefinition("reading_list", "books to read", columns:
        [
            new ColumnDefinition("title", ColumnType.Text),
            new ColumnDefinition("author", ColumnType.Text)
        ]);

        var shortlist = CollectionPrefilter.Shortlist([books, trips, conferences],
            ["event", "city", "amount_eur", "paid_on"], "here are last year's conferences");

        Assert.Equal("conferences", shortlist[0].Definition.Name);
        Assert.Contains("amount_eur", shortlist[0].MatchedFields);
        Assert.Contains("conference", shortlist[0].MatchedWords);
        Assert.True(shortlist[0].Score > 0.8);
        Assert.DoesNotContain(shortlist, static candidate => candidate.Definition.Name == "reading_list");

        // Prose with no shape yet: words alone.
        var byWords = CollectionPrefilter.Shortlist([books, trips, conferences], [], "the book by Kleppmann I want to read");
        Assert.Equal("reading_list", byWords[0].Definition.Name);

        Assert.Contains("offered:\n- conferences (fields in common: event, city, amount_eur, paid_on)", CollectionPrefilter.Describe(shortlist));
        Assert.Equal("offered: none - nothing stored looks like this", CollectionPrefilter.Describe([]));
    }

    private static Parsed<string> Ok(string text) => Parsed<string>.Ok(text);
}
