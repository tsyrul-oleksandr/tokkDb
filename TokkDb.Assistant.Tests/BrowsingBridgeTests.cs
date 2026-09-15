using TokkDb.Assistant.Agents.Browsing;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Orchestration;
using Shown = TokkDb.Assistant.Agents.Orchestration.Values;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The browser's two connections to the conversation (Phase 6.5): a change made from the browser
/// goes through exactly the rules a change said in words goes through (BR-8), and a record opened
/// there becomes the subject of the next thing said (BR-9). And S-8 in full: looking for oneself,
/// through the browsing surface's own logic, with zero tokens recorded.
/// </summary>
public sealed class BrowsingBridgeTests
{
    /// <summary>S-8: the overview, the conferences by cost, the Lviv one at 13 000 after S-5's correction, what it keeps - and no model call anywhere.</summary>
    [Fact]
    public async Task S8_looking_for_oneself_records_zero_tokens()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv());
        var corrected = await host.Say(conversation, "The Lviv one was actually 13 000.");
        Assert.True(corrected.Succeeded, corrected.Reply);

        var calls = host.Model.TotalCalls;
        var requestsBefore = host.Recorder.Unfinished().Count;

        // The browser: everything stored, the conferences sorted by cost, the Lviv one opened.
        var things = ThingsOverview.Of(host.Storage, AgentsHost.Today);
        var conferences = Assert.Single(things);
        Assert.Equal("Conferences", conferences.Title);
        Assert.Equal(4, conferences.HowMany);

        var table = new RecordTable(host.Storage, "conferences", pageSize: 50);
        Assert.Null(table.Open(new QuerySort("cost", Descending: true)));
        Assert.Equal(4, table.Total);
        Assert.Equal("PyCon Lviv", table.Rows[0].Title);

        var lviv = RecordDetail.Open(host.Storage, "conferences", table.Rows[0].Id)!;
        Assert.Equal("13000", lviv.Fields.Single(static field => field.Name == "cost").Value);

        // What it keeps, in the person's words.
        var keeps = WhatItKeeps.Describe(table.Definition);
        Assert.Contains(keeps, static field => field.Name == "cost" && field.Kind == "an amount");
        Assert.All(keeps, field => Assert.DoesNotContain(WhatItKeeps.ForbiddenWords, word => field.Sentence.Contains(word, StringComparison.OrdinalIgnoreCase)));

        // Zero tokens: no model call, and no request either - nothing was asked.
        Assert.Equal(calls, host.Model.TotalCalls);
        Assert.Equal(requestsBefore, host.Recorder.Unfinished().Count);
        Assert.Equal(corrected.RequestId, LastRequest(host, conversation));
    }

    /// <summary>BR-8: deleting from the browser is, in trace and card and journal, deleting by saying so.</summary>
    [Fact]
    public async Task BR8_a_removal_from_the_browser_is_indistinguishable_from_one_said()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var calls = host.Model.TotalCalls;
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");
        var prague = host.Storage.GetAll("conferences").Single(static record => record["name"] is "EuroPython");

        // From the browser: no model call, the same card.
        var fromBrowser = await host.Orchestrator.ChangeAsync(conversation, new DeleteRecords("conferences", [lviv.Id]), "Remove PyCon Lviv from conferences");
        Assert.True(fromBrowser.IsWaiting, fromBrowser.Reply);
        Assert.NotNull(fromBrowser.Question);
        Assert.Equal(calls, host.Model.TotalCalls);

        var applied = await host.Orchestrator.AnswerAsync(fromBrowser.RequestId, yes: true);
        Assert.True(applied.Succeeded, applied.Reply);
        Assert.Null(host.Storage.GetById("conferences", lviv.Id));

        // By saying so: the same kind of card, the same steps after the intent was worked out, the same journal entry.
        host.Model.Answer("intent", Scenarios.Intent("remove"));
        var said = await host.Orchestrator.HandleAsync(new TurnInput(conversation, "Remove the EuroPython one"));
        Assert.True(said.IsWaiting, said.Reply);
        var appliedSaid = await host.Orchestrator.AnswerAsync(said.RequestId, yes: true);
        Assert.True(appliedSaid.Succeeded, appliedSaid.Reply);
        Assert.Null(host.Storage.GetById("conferences", prague.Id));

        Assert.Equal(fromBrowser.Question.Lines.Count, said.Question!.Lines.Count);
        Assert.Equal(fromBrowser.Question.UndoNote, said.Question.UndoNote);

        var browserSteps = host.Recorder.Read(fromBrowser.RequestId)!.Value.Steps.Select(static step => step.Name).ToList();
        var saidSteps = host.Recorder.Read(said.RequestId)!.Value.Steps.Select(static step => step.Name).ToList();
        Assert.DoesNotContain(browserSteps, static name => name == "what was meant");
        Assert.Equal(saidSteps.SkipWhile(static name => name != "what it would do"), browserSteps.SkipWhile(static name => name != "what it would do"));
        Assert.All(host.Recorder.Read(fromBrowser.RequestId)!.Value.Steps, static step => Assert.Null(step.Call));

        var browserChange = Assert.Single(host.Recorder.Changes(fromBrowser.RequestId));
        var saidChange = Assert.Single(host.Recorder.Changes(said.RequestId));
        Assert.Equal(ChangeKind.Delete, browserChange.Kind);
        Assert.Equal(saidChange.Kind, browserChange.Kind);
        Assert.Equal(saidChange.Reversibility, browserChange.Reversibility);

        // The conversation reads as one story: what was done from the browser is a turn in it.
        var turns = host.Storage.Conversations.Turns(conversation);
        Assert.Contains(turns, static turn => turn.Speaker is TurnSpeaker.Person && turn.Text == "Remove PyCon Lviv from conferences");
    }

    /// <summary>BR-8: an edit from the browser is a correction - applied without asking, traced with the old value beside the new.</summary>
    [Fact]
    public async Task BR8_an_edit_from_the_browser_is_applied_silently_and_traced_with_before_and_after()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesStored(host);
        var calls = host.Model.TotalCalls;
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        Assert.True(Shown.TryRead(ColumnType.Decimal, "13000", out var value));
        var outcome = await host.Orchestrator.ChangeAsync(conversation, new ChangeRecord("conferences", lviv.Id, "cost", value), "Change the cost of PyCon Lviv to 13000");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Equal("Changed cost of PyCon Lviv to 13000.", outcome.Reply);
        Assert.Equal(13000m, host.Storage.GetById("conferences", lviv.Id)!["cost"]);
        Assert.Equal(calls, host.Model.TotalCalls);

        var change = Assert.Single(host.Recorder.Changes(outcome.RequestId));
        Assert.Equal(ChangeKind.Update, change.Kind);
        var table = RecordChangeTable.For(host.Storage, "conferences", lviv.Id, change.PreviousVersionId, change.VersionId);
        var row = Assert.Single(table.Rows);
        Assert.Equal(12000m, row.Before);
        Assert.Equal(13000m, row.After);

        var step = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "what it would do");
        Assert.Contains("reads 12000 and would read 13000", step.Output);

        // A value that does not read as the field keeps it never becomes an action; the browser refuses it first.
        Assert.False(Shown.TryRead(ColumnType.Decimal, "twelve", out _));
    }

    /// <summary>A record change held as an intent comes back as the same change (AG-3d for the browser).</summary>
    [Fact]
    public void A_record_change_round_trips_through_the_held_intent()
    {
        var id = Ulid.NewUlid();
        var intent = IntentPayloads.Structure(new ChangeRecord("conferences", id, "cost", 13000m), "v1");

        var (action, version) = IntentPayloads.ReadStructure(intent.Payload);

        var change = Assert.IsType<ChangeRecord>(action);
        Assert.Equal(id, change.Id);
        Assert.Equal("cost", change.Field);
        Assert.Equal(13000m, change.Value);
        Assert.Equal("v1", version);
        Assert.True(IntentPayloads.IsIntact(intent));
    }

    /// <summary>BR-9: a record opened in the browser is the subject of the next thing said, with no model call to make it so.</summary>
    [Fact]
    public async Task BR9_a_record_opened_in_the_browser_is_the_subject_of_the_next_turn()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesStored(host);
        var calls = host.Model.TotalCalls;
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        var shown = host.Orchestrator.LookAt(conversation, "conferences", lviv.Id);

        Assert.NotNull(shown);
        Assert.Equal(conversation, shown.Value.Conversation.Id);
        Assert.Equal("PyCon Lviv", Assert.Single(shown.Value.Shown.Titles));
        Assert.Equal(calls, host.Model.TotalCalls);

        var handle = host.Orchestrator.LastHandle(conversation);
        Assert.NotNull(handle);
        Assert.Equal(lviv.Id, Assert.Single(handle.Shown).Id);

        // "How many of those?" is answered from what is shown - the one record - and C# knows a follow-up when it sees one: no model at all.
        host.Model.Answer("intent", Scenarios.Intent("follow-up"));
        var answer = await host.Say(conversation, "how many of those?");
        Assert.True(answer.Succeeded, answer.Reply);
        Assert.Contains("One: that was all that was shown", answer.Reply);
        Assert.Equal(calls, host.Model.TotalCalls);

        // And a conversation that did not exist yet is started for it, titled by the record.
        var fresh = host.Orchestrator.LookAt(null, "conferences", lviv.Id);
        Assert.NotNull(fresh);
        Assert.Equal("Looking at PyCon Lviv", fresh.Value.Conversation.Title);
        Assert.Null(host.Orchestrator.LookAt(conversation, "conferences", Ulid.NewUlid()));
    }

    private static Ulid? LastRequest(AgentsHost host, Ulid conversation) =>
        host.Storage.Conversations.Turns(conversation).LastOrDefault(static turn => turn.RequestId is not null)?.RequestId;

    private static async Task<Ulid> GivenConferencesStored(AgentsHost host)
    {
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        var outcome = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));
        Assert.True(outcome.Succeeded, outcome.Reply);
        return outcome.ConversationId;
    }

    private static async Task<Ulid> GivenConferencesShown(AgentsHost host)
    {
        var conversation = await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        var shown = await host.Say(conversation, "How much did I spend on conferences last year?");
        Assert.True(shown.Succeeded, shown.Reply);
        return conversation;
    }
}
