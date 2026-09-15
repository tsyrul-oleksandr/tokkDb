using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Requests;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The scenarios of §5, S-1 to S-6 and S-8, and the negative ones N-1 to N-12 that a real model
/// cannot be asked to reproduce, run end to end against the fake model and the in-memory storage
/// (D-12, Phase 4's exit). Each asserts the storage effect, the trace, and - for the negative
/// ones - what the user is told, because the failure that matters is a confident wrong answer.
/// </summary>
public sealed class AssistantScenarioTests
{
    private const string Store = "store";

    // ---- S-1 to S-6 ---------------------------------------------------------------------------------------------

    /// <summary>S-1: prose in, two records out, a new thing made, and the diagram shows the steps.</summary>
    [Fact]
    public async Task S1_storing_prose_makes_a_new_thing_and_two_records()
    {
        using var host = new AgentsHost();
        host.Model.Answer("intent", Scenarios.Intent(Store)).Answer("extraction", Scenarios.ExtractedConferences);

        var outcome = await host.Say(null, "I want to save these — the conference in Lviv on 14 March, 12 000 hryvnia, and the one in Kyiv in May, 8 500.");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("Started keeping conferences", outcome.Reply);
        Assert.Contains("kept 2", outcome.Reply);

        var definition = host.Storage.GetCollectionDefinition("conferences");
        Assert.NotNull(definition);
        Assert.Equal(["name", "city", "date", "cost"], definition.Columns.Where(static column => column.Name != Fingerprints.ColumnName).Select(static column => column.Name));
        Assert.Equal(ColumnType.Decimal, definition.Column("cost")!.Type);
        Assert.Equal(ColumnType.Date, definition.Column("date")!.Type);

        var records = host.Storage.GetAll("conferences");
        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record["city"] is "Lviv" && record["cost"] is 12000m && record["date"] is DateOnly { Month: 3 });

        // The diagram: intent, extraction, mapping, creation and write (TR-1).
        var steps = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Select(static step => step.Name).ToList();
        Assert.Equal("the person", steps[0]);
        Assert.Equal("the assistant", steps[1]);
        Assert.Contains("what was meant", steps);
        Assert.Contains("what is in the text", steps);
        Assert.Contains("the placement", steps);
        Assert.Contains("the new thing", steps);
        Assert.Contains("the write", steps);
        Assert.All(host.Recorder.Read(outcome.RequestId)!.Value.Steps, static step => Assert.Equal(StepStatus.Completed, step.Status));

        // Two model calls, and the change records commit with the writes.
        Assert.Equal(2, host.Model.TotalCalls);
        Assert.Equal(3, host.Recorder.Changes(outcome.RequestId).Count);
        Assert.Equal(2, host.Recorder.Changes(outcome.RequestId).Count(static change => change.Kind is ChangeKind.Insert));
        Assert.Contains(host.Recorder.Changes(outcome.RequestId), static change => change.Kind is ChangeKind.CollectionAdded);
    }

    /// <summary>S-2: a spreadsheet into the thing S-1 made, one new column added without asking, rows in one transaction.</summary>
    [Fact]
    public async Task S2_a_spreadsheet_goes_into_the_existing_thing_with_one_new_field()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);

        var outcome = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("added a field called notes", outcome.Reply);
        Assert.Contains("Kept 4", outcome.Reply);
        Assert.Null(outcome.Question);

        Assert.NotNull(host.Storage.GetCollectionDefinition("conferences")!.Column("notes"));
        Assert.Equal(4, host.Storage.GetAll("conferences").Count);
        Assert.Contains(host.Storage.GetAll("conferences"), record => record["name"] is "PyCon Lviv" && record["cost"] is 12000m);

        // One model call: the intent was plain from the attachment (AG-1e).
        Assert.Equal(1, host.Model.TotalCalls);
        Assert.Equal("mapping", host.Model.Requests[0].Operation);

        // The rows never reached the model: a profile and a few examples did (IN-3, D-6).
        Assert.DoesNotContain("KyivJS", host.Model.Requests[0].Prompt);
        Assert.Contains("incoming: conferences_2025, 4 rows", host.Model.Requests[0].Content);
    }

    /// <summary>S-3: a question becomes a query, no rows enter a model context, and the answer carries the total.</summary>
    [Fact]
    public async Task S3_retrieval_runs_a_query_and_no_rows_enter_the_model()
    {
        using var host = new AgentsHost();
        await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);

        var outcome = await host.Say(null, "How much did I spend on conferences last year?");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.NotNull(outcome.Results);
        Assert.Equal(4, outcome.Results.Total);
        Assert.Contains("21940 in all, over 4 conferences", outcome.Reply);
        Assert.Equal(4, outcome.Results.Records.Count);

        // D-6: nothing of the records in any prompt.
        Assert.All(host.Model.Requests, request => Assert.DoesNotContain("Lviv", request.Prompt));
        Assert.All(host.Model.Requests, request => Assert.DoesNotContain("12000", request.Prompt));

        // The handle survives in the conversation, with the identities of the page shown (QR-3a).
        var handle = host.Orchestrator.LastHandle(outcome.ConversationId);
        Assert.NotNull(handle);
        Assert.Equal(4, handle.Shown.Count);
        Assert.Equal("cost", handle.TotalField);
        Assert.Contains("cost", handle.Ranges.Keys);

        // The retrieval step carries the query and the row count (TR-6).
        var looking = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "looking");
        Assert.Contains("\"thing\":\"conferences\"", looking.Input);
        Assert.Contains("4 of 4 shown", looking.Output);
    }

    /// <summary>S-4: "which was the most expensive?" is answered from what was shown, at zero model tokens.</summary>
    [Fact]
    public async Task S4_a_follow_up_is_answered_from_the_result_with_no_model_call()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var calls = host.Model.TotalCalls;

        var outcome = await host.Say(conversation, "Which was the most expensive?");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("PyCon Lviv", outcome.Reply);
        Assert.Contains("12000", outcome.Reply);
        Assert.Contains("you were shown", outcome.Reply);
        Assert.Equal(calls, host.Model.TotalCalls);

        var count = await host.Say(conversation, "how many of those?");
        Assert.Contains("4 of conferences", count.Reply);
        Assert.Equal(calls, host.Model.TotalCalls);
    }

    /// <summary>S-5: the correction finds the record without the user naming a thing or a record, and the trace shows before beside after.</summary>
    [Fact]
    public async Task S5_a_correction_updates_the_record_and_the_trace_shows_before_beside_after()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv());

        var outcome = await host.Say(conversation, "The Lviv one was actually 13 000.");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("from 12000 to 13000", outcome.Reply);

        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");
        Assert.Equal(13000m, lviv["cost"]);

        var change = Assert.Single(host.Recorder.Changes(outcome.RequestId));
        Assert.Equal(ChangeKind.Update, change.Kind);
        var table = RecordChangeTable.For(host.Storage, "conferences", lviv.Id, change.PreviousVersionId, change.VersionId);
        var row = Assert.Single(table.Rows);
        Assert.Equal("cost", row.ColumnName);
        Assert.Equal(12000m, row.Before);
        Assert.Equal(13000m, row.After);

        var step = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "the change");
        Assert.Contains("12000", step.Input);
        Assert.Contains("13000", step.Output);
    }

    /// <summary>S-6: dropping a field shows how many records hold a value, with three examples, and waits.</summary>
    [Fact]
    public async Task S6_a_destructive_change_states_the_loss_and_waits()
    {
        using var host = new AgentsHost();
        await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("restructure")).Answer("structure", Scenarios.DropNotes);

        var outcome = await host.Say(null, "Drop the notes I was keeping on these.");

        Assert.True(outcome.IsWaiting, outcome.Reply);
        Assert.NotNull(outcome.Question);
        Assert.Contains("3 of your 4 conferences have a notes", outcome.Question.Lines[0]);
        Assert.Equal(3, outcome.Question.Examples.Count);
        Assert.Contains(outcome.Question.Examples, static example => example.Contains("EuroPython", StringComparison.Ordinal));
        Assert.True(outcome.Question.CanBeUndone);
        Assert.NotNull(host.Storage.GetCollectionDefinition("conferences")!.Column("notes"));

        // UI-2: not a database word on it.
        foreach (var word in new[] { "column", "schema", "index", "query", "constraint", "nullable", "collection" })
        {
            Assert.DoesNotContain(word, (outcome.Question.Title + " " + string.Join(" ", outcome.Question.Lines)).ToLowerInvariant());
        }

        var yes = await host.Orchestrator.AnswerAsync(outcome.RequestId, yes: true);
        Assert.True(yes.Succeeded, yes.Reply);
        Assert.Null(host.Storage.GetCollectionDefinition("conferences")!.Column("notes"));
        Assert.Contains(host.Recorder.Changes(outcome.RequestId), static change => change.Kind is ChangeKind.FieldRemoved);
    }

    /// <summary>S-8's half that has no browser yet: reading costs nothing, and no model is called.</summary>
    [Fact]
    public async Task S8_reading_what_is_stored_calls_no_model()
    {
        using var host = new AgentsHost();
        await GivenConferencesStored(host);
        var calls = host.Model.TotalCalls;

        var things = host.Storage.GetCollectionDefinitions();
        var sorted = host.Storage.ExecuteQuery(new StorageQuery("conferences", orderBy: [new QuerySort("cost", Descending: true)]));
        var lviv = host.Storage.GetById("conferences", sorted.Records[0].Id);

        Assert.Single(things);
        Assert.Equal("PyCon Lviv", lviv!["name"]);
        Assert.Equal(calls, host.Model.TotalCalls);
    }

    // ---- N-1 to N-12 --------------------------------------------------------------------------------------------

    /// <summary>N-1: two candidates within the gap of each other produce a question showing both with what they matched on.</summary>
    [Fact]
    public async Task N1_two_close_candidates_produce_a_question_showing_both()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        Scenarios.GivenTrips(host.Storage);
        host.Model.Answer("mapping", """{"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"event","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");

        var outcome = await host.Say(null, "", AgentsHost.Csv("spring", "event,city,date,cost\nEuroPython,Prague,2025-07-20,840\n"));

        Assert.True(outcome.IsWaiting, outcome.Reply);
        Assert.NotNull(outcome.Question);
        Assert.Equal("Add these to conferences?", outcome.Question.Title);
        Assert.Contains(outcome.Question.Lines, static line => line.Contains("could also be trips", StringComparison.Ordinal));
        Assert.Contains(outcome.Question.Lines, static line => line.Contains("in common", StringComparison.Ordinal));
        Assert.Empty(host.Storage.GetAll("conferences"));
        Assert.Empty(host.Storage.GetAll("trips"));

        var where = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "the placement");
        Assert.Contains("asked", where.Output);
    }

    /// <summary>N-2: a plausible, wrong placement is visible in the reply and the diagram, and correctable in one turn.</summary>
    [Fact]
    public async Task N2_a_wrong_placement_is_visible_and_undone_in_one_turn()
    {
        using var host = new AgentsHost();
        Scenarios.GivenTrips(host.Storage);
        host.Model.Answer("mapping", """{"choice":"trips","newName":"","purpose":"","fields":[{"incoming":"name","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");

        var outcome = await host.Say(null, "", AgentsHost.Csv("conferences", "name,city,date,cost\nEuroPython,Prague,2025-07-20,840\nDevDays,Vilnius,2025-05-12,600\n"));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("trips", outcome.Reply);
        Assert.Contains("trips", host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "the placement").Output);
        Assert.Equal(2, host.Storage.GetAll("trips").Count);

        var undo = await host.Say(outcome.ConversationId, "undo that");
        Assert.True(undo.Succeeded, undo.Reply);
        Assert.Contains("Put 2 of trips back", undo.Reply);
        Assert.Empty(host.Storage.GetAll("trips"));
    }

    /// <summary>N-3: the same file twice - 22 stored, then 0, reported as skipped per row with the key that matched.</summary>
    [Fact]
    public async Task N3_the_same_file_twice_stores_nothing_twice_and_says_so_per_row()
    {
        using var host = new AgentsHost();
        var csv = "name,city,date,cost\n" + string.Join("\n", Enumerable.Range(1, 22).Select(i => $"Conf {i},City {i},2025-01-{i:00},{100 * i}")) + "\n";

        var first = await host.Say(null, "", AgentsHost.Csv("conferences", csv));
        Assert.True(first.Succeeded, first.Reply);
        Assert.Equal(22, host.Storage.GetAll("conferences").Count);

        host.Model.Answer("mapping", """{"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"name","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");
        var second = await host.Say(first.ConversationId, "", AgentsHost.Csv("conferences", csv));

        Assert.True(second.Succeeded, second.Reply);
        Assert.Equal(22, host.Storage.GetAll("conferences").Count);
        Assert.Contains("skipped 22 already there, matched on everything in the row", second.Reply);
        Assert.Empty(host.Recorder.Changes(second.RequestId).Where(static change => change.IsRecordChange));

        // Undoing the second import is a no-op: every row was skipped (IN-8a).
        var undo = await host.Say(first.ConversationId, "undo");
        Assert.Equal(22, host.Storage.GetAll("conferences").Count);

        // Per row, not as a count: the write step lists what happened to each skipped row.
        var write = host.Recorder.Read(second.RequestId)!.Value.Steps.Single(static step => step.Name == "the write");
        Assert.Contains("skipped 22", write.Output);
    }

    /// <summary>N-4: 100 rows of which 3 are malformed: 97 stored, 3 reported with line numbers and reasons, in one batch.</summary>
    [Fact]
    public async Task N4_a_malformed_file_stores_the_good_rows_and_reports_the_bad_ones_by_line()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        var rows = Enumerable.Range(1, 100).Select(i => i is 12 or 40 or 41
            ? $"Conf {i},City {i},not a date,{100 * i}"
            : $"Conf {i},City {i},2025-06-{(i % 28) + 1:00},{100 * i}");
        var csv = "name,city,date,cost\n" + string.Join("\n", rows) + "\n";
        host.Model.Answer("mapping", """{"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"name","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");

        var outcome = await host.Say(null, "", AgentsHost.Csv("conferences", csv));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Equal(97, host.Storage.GetAll("conferences").Count);
        Assert.Contains("Kept 97", outcome.Reply);
        Assert.Contains("3 could not be kept", outcome.Reply);
        Assert.Contains("line 13:", outcome.Reply);
        Assert.Contains("not a date", outcome.Reply);
        Assert.Equal(97, host.Recorder.Changes(outcome.RequestId).Count(static change => change.Kind is ChangeKind.Insert));
    }

    /// <summary>N-5: headers and no rows, and a message with nothing in it: neither creates anything, both say so.</summary>
    [Fact]
    public async Task N5_an_empty_file_and_an_empty_message_create_nothing_and_say_so()
    {
        using var host = new AgentsHost();

        var file = await host.Say(null, "", AgentsHost.Csv("empty", "name,city,date,cost\n"));
        Assert.True(file.Succeeded, file.Reply);
        Assert.Contains("nothing in it", file.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());

        var message = await host.Say(null, "   ");
        Assert.True(message.State is RequestState.Completed, message.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());
        Assert.Equal(0, host.Model.TotalCalls);
    }

    /// <summary>N-6: the model fails - malformed past the bound, and a timeout - and neither leaves a partial write.</summary>
    [Fact]
    public async Task N6_a_failing_model_ends_with_a_message_and_no_partial_write()
    {
        using var host = new AgentsHost();
        host.Model.Answer("intent", Scenarios.Intent(Store)).Malformed("extraction", 5);

        var malformed = await host.Say(null, "keep this: a conference in Lviv for 12 000");

        Assert.Equal(RequestState.Failed, malformed.State);
        Assert.Contains("could not be used", malformed.Reply);
        Assert.Contains("Nothing was changed", malformed.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());
        Assert.Equal(StepStatus.Failed, host.Recorder.Read(malformed.RequestId)!.Value.Steps.Single(static step => step.Name == "what is in the text").Status);

        host.Model.Answer("intent", Scenarios.Intent(Store)).Timeout("extraction");
        var timeout = await host.Say(null, "keep this: a conference in Kyiv for 8 500");

        Assert.Equal(RequestState.Failed, timeout.State);
        Assert.Contains("did not answer in time", timeout.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());
    }

    /// <summary>N-7: cancelled mid-request: nothing is half-applied and the state is Cancelled.</summary>
    [Fact]
    public async Task N7_a_cancelled_request_applies_nothing_and_reaches_cancelled()
    {
        using var host = new AgentsHost();
        host.Model.Latency = TimeSpan.FromSeconds(5);
        host.Model.Answer("intent", Scenarios.Intent(Store)).Answer("extraction", Scenarios.ExtractedConferences);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await host.Orchestrator.HandleAsync(new TurnInput(null, "keep these conferences"), cancel.Token);

        Assert.Equal(RequestState.Cancelled, outcome.State);
        Assert.Equal(Replies.Cancelled(), outcome.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());
        Assert.Empty(host.Recorder.Changes(outcome.RequestId));
    }

    /// <summary>N-8: the user says no. Nothing happens, the request completes, and the refusal is in the trace.</summary>
    [Fact]
    public async Task N8_a_refused_confirmation_changes_nothing_and_is_in_the_trace()
    {
        using var host = new AgentsHost();
        await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("restructure")).Answer("structure", Scenarios.DropNotes);
        var asked = await host.Say(null, "Drop the notes.");
        Assert.True(asked.IsWaiting, asked.Reply);

        var no = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: false);

        Assert.Equal(RequestState.Completed, no.State);
        Assert.Equal(Replies.Declined(), no.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("conferences")!.Column("notes"));
        Assert.Empty(host.Recorder.Changes(asked.RequestId));

        var request = host.Recorder.Request(asked.RequestId)!;
        Assert.Equal("declined by the user", request.Reason);
        Assert.Contains(host.Recorder.Read(asked.RequestId)!.Value.Steps, static step => step.Name == "the answer" && step.Input == "no");
    }

    /// <summary>N-10: the shape changed between the question and the answer: the request re-plans rather than applying blind.</summary>
    [Fact]
    public async Task N10_a_schema_change_underneath_makes_the_request_re_plan()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        Scenarios.GivenTrips(host.Storage);
        host.Model.Answer("mapping", """{"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"event","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");
        var asked = await host.Say(null, "", AgentsHost.Csv("spring", "event,city,date,cost\nEuroPython,Prague,2025-07-20,840\n"));
        Assert.True(asked.IsWaiting, asked.Reply);

        // The schema moves while the question is open.
        host.Storage.AddColumn("conferences", new ColumnDefinition("notes", ColumnType.Text));

        var answered = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);

        var steps = host.Recorder.Read(asked.RequestId)!.Value.Steps;
        Assert.Contains(steps, static step => step.Name == "re-planned");
        Assert.NotEqual(RequestState.Failed, answered.State);

        // Re-planned against the new shape: either applied there, or asked again - never applied blind.
        if (answered.Succeeded) Assert.Single(host.Storage.GetAll("conferences"));
        else Assert.True(answered.IsWaiting);
    }

    /// <summary>N-12: the proposal is replaced between the confirmation and the write; the write is refused on the hash.</summary>
    [Fact]
    public async Task N12_a_substituted_proposal_is_refused_on_the_hash()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        Scenarios.GivenTrips(host.Storage);
        host.Model.Answer("mapping", """{"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"event","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"}]}""");
        var asked = await host.Say(null, "", AgentsHost.Csv("spring", "event,city,date,cost\nEuroPython,Prague,2025-07-20,840\n"));
        Assert.True(asked.IsWaiting, asked.Reply);

        // The substitution: the same hash, different rows.
        var request = host.Recorder.Request(asked.RequestId)!;
        var substituted = request.Intent!.Payload.Replace("EuroPython", "Somewhere Else", StringComparison.Ordinal);
        Assert.NotEqual(request.Intent.Payload, substituted);
        host.Recorder.Move(request.Id, RequestState.WaitingForUser, request.Transitions, intent: request.Intent with { Payload = substituted });

        var answered = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);

        Assert.Equal(RequestState.Failed, answered.State);
        Assert.Contains("not what you were shown", answered.Reply);
        Assert.Empty(host.Storage.GetAll("conferences"));
        Assert.Empty(host.Storage.GetAll("trips"));
        Assert.Equal(1, host.Model.TotalCalls);
    }

    // ---- AG-8a: answering twice --------------------------------------------------------------------------------

    [Fact]
    public async Task Answering_the_same_question_twice_writes_once()
    {
        using var host = new AgentsHost();
        await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("restructure")).Answer("structure", Scenarios.DropNotes);
        var asked = await host.Say(null, "Drop the notes.");

        var first = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);
        var second = await host.Orchestrator.AnswerAsync(asked.RequestId, yes: true);

        Assert.True(first.Succeeded, first.Reply);
        Assert.Equal(RequestState.Completed, second.State);
        Assert.Equal(Replies.AlreadyDone(), second.Reply);
        Assert.Single(host.Recorder.Changes(asked.RequestId));
    }

    // ---- The scenery -------------------------------------------------------------------------------------------

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
