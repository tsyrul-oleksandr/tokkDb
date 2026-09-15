using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// N-11 and step 4.7 at the orchestrator: an import reversed, the same after an unrelated change,
/// refused whole after a change to those records naming every one, the rest offered on a card of
/// its own and taken only from that card, and a unique collision named.
/// </summary>
public sealed class UndoFlowTests
{
    /// <summary>Papers with a DOI that is already the key: an identifier column not yet unique would be asked about first (IN-6b, D-14).</summary>
    private static async Task<(AgentsHost Host, Ulid Conversation, TurnOutcome Import)> GivenAnImport(int rows = 6, bool unique = true)
    {
        var host = new AgentsHost();
        host.Storage.CreateCollection(new CollectionDefinition("papers", "papers I read", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, required: true),
            new ColumnDefinition("doi", ColumnType.Text, unique: unique),
            new ColumnDefinition("year", ColumnType.Integer)
        ]));
        host.Model.Answer("mapping", """{"choice":"papers","newName":"","purpose":"","fields":[{"incoming":"title","existing":"title"},{"incoming":"doi","existing":"doi"},{"incoming":"year","existing":"year"}]}""");

        var csv = "title,doi,year\n" + string.Join("\n", Enumerable.Range(1, rows).Select(i => $"Paper {i},10.1/{i},{2000 + i}")) + "\n";
        var import = await host.Say(null, "", AgentsHost.Csv("papers", csv));
        Assert.True(import.Succeeded, import.Reply);
        return (host, import.ConversationId, import);
    }

    [Fact]
    public async Task An_import_is_reversed_and_the_storage_is_as_it_was()
    {
        var (host, conversation, _) = await GivenAnImport();
        using (host)
        {
            var before = host.Storage.GetAll("papers").Count;
            Assert.Equal(6, before);

            var undo = await host.Say(conversation, "undo that import");

            Assert.True(undo.Succeeded, undo.Reply);
            Assert.Contains("Put 6 of papers back", undo.Reply);
            Assert.Empty(host.Storage.GetAll("papers"));
            Assert.Equal(6, host.Recorder.Changes(undo.RequestId).Count(static change => change.Kind is ChangeKind.Delete));
        }
    }

    [Fact]
    public async Task An_import_is_reversed_after_an_unrelated_change()
    {
        var (host, conversation, _) = await GivenAnImport();
        using (host)
        {
            var older = host.Storage.Create("papers", new Dictionary<string, object?> { ["title"] = "older", ["year"] = 1999L });
            host.Storage.Update(older.With("title", "older, edited"));

            var undo = await host.Say(conversation, "undo");

            Assert.True(undo.Succeeded, undo.Reply);
            Assert.Equal([older.Id], host.Storage.GetAll("papers").Select(static record => record.Id));
        }
    }

    /// <summary>After a change to those records - one edited - the undo refuses whole, names the row, offers the rest, and the partial runs only from its own card.</summary>
    [Fact]
    public async Task A_touched_record_refuses_the_undo_and_the_rest_is_offered_on_a_card_of_its_own()
    {
        var (host, conversation, _) = await GivenAnImport();
        using (host)
        {
            var touched = host.Storage.GetAll("papers").Single(static record => record["title"] is "Paper 3");
            host.Storage.Update(touched.With("year", 2030L));

            var refused = await host.Say(conversation, "undo that import");

            Assert.True(refused.IsWaiting, refused.Reply);
            Assert.NotNull(refused.Question);
            Assert.Contains("Paper 3", refused.Reply);
            Assert.Contains("has been changed since", refused.Reply);
            Assert.Equal("Put back the other 5 and leave 1 as they are?", refused.Question.Title);
            Assert.Equal(6, host.Storage.GetAll("papers").Count);

            // The offer alone did nothing (AG-11b): only the answer on the card does.
            var yes = await host.Orchestrator.AnswerAsync(refused.RequestId, yes: true);

            Assert.True(yes.Succeeded, yes.Reply);
            Assert.Contains("Put 5 of papers back", yes.Reply);
            Assert.Contains("Left as they are: Paper 3", yes.Reply);
            Assert.Equal(["Paper 3"], host.Storage.GetAll("papers").Select(static record => record["title"]));
            Assert.Contains(host.Recorder.Read(refused.RequestId)!.Value.Steps, static step => step.Name == "the answer" && step.Input == "yes");
        }
    }

    /// <summary>A record changed and changed back still blocks: the version moved, whatever the values say.</summary>
    [Fact]
    public async Task A_record_changed_and_changed_back_blocks_the_undo()
    {
        var (host, conversation, _) = await GivenAnImport();
        using (host)
        {
            var paper = host.Storage.GetAll("papers").Single(static record => record["title"] is "Paper 2");
            host.Storage.Update(paper.With("year", 2030L));
            host.Storage.Update(paper.With("year", 2002L));

            var refused = await host.Say(conversation, "undo");

            Assert.True(refused.IsWaiting, refused.Reply);
            Assert.Contains("Paper 2", refused.Reply);
            Assert.Equal(6, host.Storage.GetAll("papers").Count);
        }
    }

    /// <summary>An undo that would put back a unique value another record has taken since refuses whole and names that record.</summary>
    [Fact]
    public async Task A_unique_value_taken_since_is_named()
    {
        var (host, conversation, _) = await GivenAnImport(rows: 2, unique: true);
        using (host)
        {
            var one = host.Storage.GetAll("papers").Single(static record => record["title"] is "Paper 1");
            host.Model.Answer("intent", Scenarios.Intent("correct"));
            // A later request changes Paper 1's doi; then an outsider takes the old one.
            host.Storage.Update(one.With("doi", "10.1/9"));
            var taker = host.Storage.Create("papers", new Dictionary<string, object?> { ["title"] = "Taker", ["doi"] = "10.1/1", ["year"] = 2010L });

            var refused = await host.Say(conversation, "undo the import");

            Assert.Contains("Paper 1", refused.Reply);
            Assert.Contains("has been changed since", refused.Reply);
            Assert.NotNull(host.Storage.GetById("papers", taker.Id));
        }
    }

    /// <summary>A declined partial offer leaves everything as it is (N-8).</summary>
    [Fact]
    public async Task A_declined_partial_offer_changes_nothing()
    {
        var (host, conversation, _) = await GivenAnImport();
        using (host)
        {
            var touched = host.Storage.GetAll("papers").First();
            host.Storage.Update(touched.With("year", 2030L));
            var refused = await host.Say(conversation, "undo");
            Assert.True(refused.IsWaiting);

            var no = await host.Orchestrator.AnswerAsync(refused.RequestId, yes: false);

            Assert.Equal(RequestState.Completed, no.State);
            Assert.Equal(6, host.Storage.GetAll("papers").Count);
            Assert.Empty(host.Recorder.Changes(refused.RequestId));
        }
    }
}
