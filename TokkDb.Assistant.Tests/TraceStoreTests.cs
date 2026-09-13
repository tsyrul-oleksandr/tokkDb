using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;
using TokkDb.Pages;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The trace as it is written down (TR-1, TR-2, TR-4d, TR-7, TR-8, step 3.2).
///
/// Everything here is about the database rather than about the model. A trace whose steps are
/// right in memory and wrong after a reopen is TR-4d's failure, and it is the one that cannot be
/// caught by testing the model: the diagram of an earlier request has to be there when the
/// application is opened again, and the change journal has to outlive the diagnostics that
/// describe it.
/// </summary>
public sealed class TraceStoreTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("traces");

    public void Dispose() => _database.Dispose();

    private TokkDbStorage Open() => new(_database.FilePath);

    // ---- One request, and everything about it, after a close and a reopen ------------------------

    /// <summary>
    /// Step 3.2's acceptance condition: <b>a hand-built trace of eight steps survives a close and
    /// reopen whole</b>.
    ///
    /// The steps are TR-1's own list - the user, the orchestrator, intent, profiling, mapping, the
    /// structural change, the write, the answer - and whole means all of it: the order, the edges
    /// between them, the statuses, what went in and came out, and the model call on the step that
    /// made one, down to its token counts.
    /// </summary>
    [Fact]
    public void A_trace_of_eight_steps_survives_a_close_and_reopen_whole()
    {
        var conversation = Ulid.NewUlid();
        Ulid request;

        using (var storage = Open())
        {
            var traces = storage.Traces;
            var trace = traces.Begin(conversation, "ingest a file");
            request = trace.Id;

            foreach (var step in EightSteps(request)) traces.Record(step);

            Assert.NotNull(traces.Move(request, RequestState.Completed, expectedTransitions: 0));
        }

        using (var storage = Open())
        {
            var read = storage.Traces.Read(request);

            Assert.NotNull(read);
            var (reopened, steps) = read.Value;

            Assert.Equal(conversation, reopened.ConversationId);
            Assert.Equal("ingest a file", reopened.Operation);
            Assert.Equal(RequestState.Completed, reopened.State);
            Assert.Equal(1, reopened.Transitions);
            Assert.True(reopened.IsFinished);
            Assert.NotNull(reopened.EndedAt);

            Assert.Equal(8, steps.Count);

            Assert.Equal(
                ["the person", "the assistant", "what was meant", "what is in the file",
                 "where it belongs", "the new collection", "the write", "the answer"],
                steps.Select(static step => step.Name));

            // The edges: every step but the first names the one it followed, which is what the
            // diagram is drawn from.
            Assert.Null(steps[0].After);
            for (var index = 1; index < steps.Count; index++)
            {
                Assert.Equal(steps[index - 1].Id, steps[index].After);
            }

            Assert.All(steps, static step => Assert.Equal(StepStatus.Completed, step.Status));
            Assert.All(steps, static step => Assert.NotNull(step.EndedAt));
            Assert.Equal("expenses-q3.csv", steps[2].Input);
            Assert.Equal("a new collection, and eight rows", steps[7].Output);

            // The model calls, with their counts and their prompt hashes and no prompt.
            var calls = steps.Where(static step => step.Call is not null).Select(static step => step.Call!).ToList();

            Assert.Equal(3, calls.Count);
            Assert.Equal(["qwen3.5:4b", "qwen3.5:4b", "qwen3.5:4b"], calls.Select(static call => call.Model));
            Assert.Equal(412 + 1_180 + 96, calls.Sum(static call => call.PromptTokens));
            Assert.All(calls, static call => Assert.Equal(Hashes.Length, call.PromptHash.Length));

            var profiling = steps[3].Call!;
            Assert.Equal(TimeSpan.FromSeconds(6.5), profiling.Duration);
            Assert.Equal(4, profiling.RoundTrips);
            Assert.Equal(1, profiling.Retries);
            Assert.Equal(3_400, profiling.PeakContextTokens);
        }
    }

    /// <summary>
    /// TR-1's list, as a request that ingests a file actually produces it. The first step is the
    /// user and the second the orchestrator, and the rest are the work.
    /// </summary>
    private static List<ExecutionStep> EightSteps(Ulid request)
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-30);
        var steps = new List<ExecutionStep>();

        void Step(string name, string? input = null, string? output = null, ModelCall? call = null)
        {
            var at = started.AddSeconds(steps.Count * 2);

            steps.Add(new ExecutionStep(Ulid.NewUlid(), request, name, StepStatus.Completed, at, at.AddSeconds(1))
            {
                After = steps.Count == 0 ? null : steps[^1].Id,
                Input = input,
                Output = output,
                Call = call
            });
        }

        Step("the person", input: "here are my expenses for the quarter");
        Step("the assistant", output: "read the file, then decide where it goes");
        Step("what was meant", "expenses-q3.csv",
            "store a table",
            ModelCalls.For("qwen3.5:4b", "which of these is being asked for? ...", 412, 38, TimeSpan.FromSeconds(2.4)));
        Step("what is in the file", "8 rows, 5 columns", "date, description, amount, nights, receipt",
            ModelCalls.For("qwen3.5:4b", "name the columns of this table: ...", 1_180, 210, TimeSpan.FromSeconds(6.5)) with
            {
                RoundTrips = 4,
                Retries = 1,
                PeakContextTokens = 3_400
            });
        Step("where it belongs", "date, description, amount, nights, receipt", "a new collection, expenses",
            ModelCalls.For("qwen3.5:4b", "does this belong with anything stored? ...", 96, 12, TimeSpan.FromSeconds(1.1)));
        Step("the new collection", "expenses", "created, five columns");
        Step("the write", "8 rows", "8 written, 0 rejected");
        Step("the answer", output: "a new collection, and eight rows");

        return steps;
    }

    // ---- Two retentions -------------------------------------------------------------------------

    /// <summary>
    /// Step 3.2's second acceptance condition, and TR-8: <b>purging diagnostics leaves every
    /// DataChange intact</b>.
    ///
    /// And the dangling identifier TR-2 asks for rather than warns against: the trace is gone, so
    /// <see cref="ITraceRecorder.Read"/> answers null and the interface renders "the diagram for
    /// this change is no longer kept", while every change is still attributable to the request
    /// that made it and the moment it happened.
    /// </summary>
    [Fact]
    public void Purging_the_diagnostics_leaves_every_change_intact()
    {
        var request = Written(finished: true, changes: 6);

        using (var storage = Open())
        {
            var traces = storage.Traces;

            Assert.NotNull(traces.Read(request));
            Assert.Equal(6, traces.Changes(request).Count);

            Assert.Equal(1, traces.PurgeDiagnostics(DateTimeOffset.UtcNow));

            Assert.Null(traces.Read(request));

            var changes = traces.Changes(request);

            Assert.Equal(6, changes.Count);
            Assert.All(changes, change => Assert.Equal(request, change.RequestId));
            Assert.All(changes, static change => Assert.NotEqual(default, change.At));
        }

        // And after a reopen, which is where a purge that only emptied a cache would show.
        using (var storage = Open())
        {
            Assert.Null(storage.Traces.Read(request));
            Assert.Equal(6, storage.Traces.Changes(request).Count);
        }
    }

    /// <summary>
    /// A purge is defined by time and by <b>being finished</b>. A request can sit in
    /// <see cref="RequestState.WaitingForUser"/> for as long as the person takes, and how long ago
    /// it started says nothing about whether its diagram is still wanted (D-15).
    /// </summary>
    [Fact]
    public void A_request_that_has_not_finished_is_not_purged()
    {
        var waiting = Written(finished: false, changes: 1);
        var finished = Written(finished: true, changes: 1);

        using var storage = Open();

        Assert.Equal(1, storage.Traces.PurgeDiagnostics(DateTimeOffset.UtcNow));

        var kept = storage.Traces.Read(waiting);

        Assert.NotNull(kept);
        Assert.Equal(RequestState.WaitingForUser, kept.Value.Request.State);

        // And a purge that took one request's steps with another's would be worse than one that
        // took nothing, because the diagram would be wrong rather than absent.
        Assert.Single(kept.Value.Steps);

        Assert.Null(storage.Traces.Read(finished));
    }

    /// <summary>
    /// What a purge is allowed to touch, asserted against the list rather than against this
    /// implementation's behaviour: the diagnostics, and nothing that is the audit record or the
    /// user's own (TR-7, TR-8, SC-10).
    /// </summary>
    [Fact]
    public void The_reserved_collections_are_the_five_the_assistant_owns_in_three_retentions()
    {
        Assert.Equal(5, TraceCollections.Reserved.Count);

        Assert.All(TraceCollections.Reserved.Keys, static name => Assert.Contains(name, SystemCollections.All));

        Assert.Equal(
            [SystemCollections.Traces, SystemCollections.TraceSteps],
            TraceCollections.Prunable);

        Assert.Equal(RetentionClass.Journal, TraceCollections.ClassOf(SystemCollections.DataChanges));
        Assert.Equal(RetentionClass.UserOwned, TraceCollections.ClassOf(SystemCollections.Conversations));
        Assert.Equal(RetentionClass.UserOwned, TraceCollections.ClassOf(SystemCollections.ConversationEntries));
    }

    /// <summary>
    /// Conversations are on no window at all, so a purge leaves them exactly as they were (SC-10).
    /// Data is not in this method's reach either, and the assertion is here because "purge removes
    /// old diagnostics and leaves everything else" is a claim about what it does not do.
    /// </summary>
    [Fact]
    public void A_purge_leaves_the_data_and_the_conversations_alone()
    {
        using var storage = Open();

        storage.CreateCollection(new CollectionDefinition("expenses", columns:
            [new ColumnDefinition("description", ColumnType.Text, required: true)]));

        var record = storage.Create("expenses", new Dictionary<string, object?> { ["description"] = "tickets" });

        var conversation = storage.Conversations.Start("the quarter's expenses");
        storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, "here they are");

        var request = storage.Traces.Begin(conversation.Id, "ingest a file");
        storage.Traces.Move(request.Id, RequestState.Completed, expectedTransitions: 0);

        Assert.Equal(1, storage.Traces.PurgeDiagnostics(DateTimeOffset.UtcNow));

        Assert.NotNull(storage.GetById("expenses", record.Id));
        Assert.NotNull(storage.Conversations.Get(conversation.Id));
        Assert.Single(storage.Conversations.Turns(conversation.Id));
    }

    // ---- The request as a state machine -----------------------------------------------------------

    /// <summary>
    /// A transition is a compare-and-swap on the counter, not a write (AG-8a).
    ///
    /// The counter rather than the state, because two answers to the same confirmation both move
    /// it out of <see cref="RequestState.WaitingForUser"/> and a check on the state alone would
    /// let the second one through. Step 3.4 puts the lock and the recovery around this.
    /// </summary>
    [Fact]
    public void A_second_answer_to_the_same_question_does_not_win_a_race()
    {
        using var storage = Open();
        var traces = storage.Traces;

        var request = traces.Begin(Ulid.NewUlid(), "delete some records");

        var waiting = traces.Move(request.Id, RequestState.WaitingForUser, expectedTransitions: 0);
        Assert.NotNull(waiting);
        Assert.Equal(1, waiting.Transitions);

        var first = traces.Move(request.Id, RequestState.Resuming, expectedTransitions: 1);
        Assert.NotNull(first);

        // The second answer arrives with the same expectation the first one had, and loses.
        Assert.Null(traces.Move(request.Id, RequestState.Resuming, expectedTransitions: 1));

        var read = traces.Read(request.Id);
        Assert.Equal(RequestState.Resuming, read!.Value.Request.State);
        Assert.Equal(2, read.Value.Request.Transitions);
    }

    // ---- The journal on disk ------------------------------------------------------------------------

    /// <summary>
    /// The size of the journal, measured on disk rather than argued about: <b>the same import of
    /// two thousand records costs the same whether the rows are five columns wide or sixty</b>.
    ///
    /// <see cref="ChangeJournalTests"/> asserts the payload rule at ten thousand records, where it
    /// is a property of the model. This asserts that nothing between the model and the file
    /// quietly puts the row back - and that the journal of an import is a small fraction of the
    /// data it describes.
    /// </summary>
    [Fact]
    public void The_journal_of_an_import_is_the_same_size_whatever_the_rows_are_like()
    {
        const int records = 2_000;

        var narrow = Journal(records, 5, 20);
        var wide = Journal(records, 60, 200);

        var data = (long)records * 60 * 200;

        Assert.True(
            Math.Abs(narrow - wide) < narrow / 10,
            $"a narrow import journals {narrow} bytes and a wide one {wide}");

        Assert.True(wide < data / 20, $"the journal of {records} wide rows is {wide} bytes against {data} of data");
    }

    /// <summary>Journals an import of that shape into a database of its own, and returns the size of the file.</summary>
    private static long Journal(int records, int columns, int width)
    {
        using var database = new TemporaryDatabase($"journal-{columns}x{width}");

        var row = Enumerable.Range(0, columns).ToDictionary(
            column => $"column{column:00}",
            column => (object?)new string((char)('a' + column % 26), width),
            StringComparer.Ordinal);

        using (var storage = new TokkDbStorage(database.FilePath))
        {
            var request = storage.Traces.Begin(Ulid.NewUlid(), "ingest a file");

            // One unit of work, the way an import runs: the change records commit with the
            // records they describe rather than one commit at a time (SC-5, TR-4).
            storage.InUnitOfWork(() =>
            {
                for (var record = 0; record < records; record++)
                {
                    storage.Traces.Record(DataChanges.Insert(request.Id, "exports", Ulid.NewUlid(), row));
                }
            });
        }

        return new FileInfo(database.FilePath).Length;
    }

    /// <summary>
    /// TR-4's mechanism, in the ordinary case: a change recorded inside a unit of work is in the
    /// transaction of the mutation it describes, because it is the same transaction.
    ///
    /// The injected failure that proves neither can exist without the other is step 3.3's.
    /// </summary>
    [Fact]
    public void A_change_recorded_in_a_unit_of_work_commits_with_the_mutation()
    {
        var conversation = Ulid.NewUlid();
        Ulid request;
        Ulid record;
        Ulid write;

        using (var storage = Open())
        {
            storage.CreateCollection(new CollectionDefinition("expenses", columns:
            [
                new ColumnDefinition("description", ColumnType.Text, required: true),
                new ColumnDefinition("cost", ColumnType.Decimal)
            ]));

            request = storage.Traces.Begin(conversation, "add an expense").Id;

            var at = DateTimeOffset.UtcNow;
            var step = new ExecutionStep(Ulid.NewUlid(), request, "the write", StepStatus.Completed, at, at.AddSeconds(1));

            storage.Traces.Record(step);
            write = step.Id;

            record = storage.InUnitOfWork(() =>
            {
                var written = storage.Create("expenses", new Dictionary<string, object?>
                {
                    ["description"] = "EuroPython tickets",
                    ["cost"] = 840.50m
                });

                storage.Traces.Record(
                    DataChanges.Insert(request, "expenses", written.Id, written.Fields) with { StepId = write });

                return written.Id;
            });
        }

        using (var storage = Open())
        {
            var stored = storage.GetById("expenses", record);
            var change = Assert.Single(storage.Traces.Changes(request));

            Assert.NotNull(stored);
            Assert.Equal(record, change.RecordId);

            // TR-2a's other half: the block a person clicks in the diagram is the one that made
            // this change. The join that has to survive a purge is the request; this is the finer
            // one, and it is allowed to dangle once the diagram is gone.
            Assert.Equal(write, change.StepId);
            Assert.Equal("expenses", change.CollectionName);
            Assert.Equal(ChangeKind.Insert, change.Kind);

            // The record as it was written, so that "has this been touched since" is answerable
            // against what is in storage now (AG-11a).
            Assert.Equal(Hashes.OfFields(stored.Fields), change.ContentHash);
        }
    }

    /// <summary>
    /// A delete keeps the whole record through a reopen, values and types and all, which is the
    /// half of D-17 the database has to hold up rather than the model.
    /// </summary>
    [Fact]
    public void A_deleted_record_can_still_be_put_back_after_a_reopen()
    {
        var removed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["description"] = "Hotel, four nights",
            ["cost"] = 1234.56m,
            ["nights"] = 4L,
            ["paidOn"] = new DateOnly(2026, 7, 21),
            ["reimbursed"] = false,
            ["conference"] = Ulid.NewUlid()
        };

        Ulid request;

        using (var storage = Open())
        {
            request = storage.Traces.Begin(Ulid.NewUlid(), "delete an expense").Id;
            storage.Traces.Record(DataChanges.Delete(request, "expenses", Ulid.NewUlid(), removed));
        }

        using (var storage = Open())
        {
            var change = Assert.Single(storage.Traces.Changes(request));

            Assert.Equal(Reversibility.ReversibleWithConditions, change.Reversibility);

            var restored = DataChanges.Restore(change);

            Assert.Equal(removed.Count, restored.Count);

            foreach (var (name, value) in removed)
            {
                Assert.Equal(value, restored[name]);
                Assert.Equal(value!.GetType(), restored[name]!.GetType());
            }
        }
    }

    /// <summary>
    /// Changes come back in the order they were made, because an undo replays them backwards.
    /// </summary>
    [Fact]
    public void The_changes_of_a_request_come_back_in_the_order_they_were_made()
    {
        using var storage = Open();

        var request = storage.Traces.Begin(Ulid.NewUlid(), "import a file").Id;
        var records = Enumerable.Range(0, 20).Select(static _ => Ulid.NewUlid()).ToList();

        foreach (var record in records)
        {
            storage.Traces.Record(DataChanges.Insert(request, "expenses", record,
                new Dictionary<string, object?> { ["description"] = record.ToString() }));
        }

        Assert.Equal(records, storage.Traces.Changes(request).Select(static change => change.RecordId!.Value));
    }

    /// <summary>One request's diagnostics and changes, written and left behind.</summary>
    private Ulid Written(bool finished, int changes)
    {
        using var storage = Open();

        var request = storage.Traces.Begin(Ulid.NewUlid(), "import a file");

        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request.Id, "the write", StepStatus.Completed, at, at.AddSeconds(2)));

        for (var change = 0; change < changes; change++)
        {
            storage.Traces.Record(DataChanges.Insert(request.Id, "expenses", Ulid.NewUlid(),
                new Dictionary<string, object?> { ["description"] = $"row {change}" }));
        }

        if (finished)
        {
            storage.Traces.Move(request.Id, RequestState.Completed, expectedTransitions: 0);
        }
        else
        {
            storage.Traces.Move(request.Id, RequestState.WaitingForUser, expectedTransitions: 0);
        }

        return request.Id;
    }
}
