using TokkDb.Assistant.Agents.Requests;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;
using TokkDb.Disk;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Two durability rules, not one (TR-4, TR-4a, TR-4b, TR-4c, D-11, step 3.3).
///
/// The change record commits with the mutation it describes and neither can exist without the
/// other; the lifecycle and the diagnostic steps are persisted independently as they happen,
/// linked by the request id. A kill is simulated two ways, and both are the proxy stated rather
/// than a claim: what a second, read-only connection sees while the first is still open is what
/// is on disk, and what a reopen after an unfinished close finds is what a restart would find,
/// because every transition and every immediate step is its own committed transaction.
/// </summary>
public sealed class DurabilityTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("durability");

    public void Dispose() => _database.Dispose();

    private TokkDbStorage Open(TraceDurability? durability = null) => new(_database.FilePath, durability);

    /// <summary>What is on disk right now, seen from outside the writing connection.</summary>
    private TokkDbStorage Observe()
    {
        var connection = new TokkDbConnection(_database.FilePath, TokkDbAccessMode.ReadOnly);
        connection.Load();
        return new TokkDbStorage(connection, ownsConnection: true);
    }

    private static void GivenExpenses(IStorage storage) =>
        storage.CreateCollection(new CollectionDefinition("expenses", columns:
        [
            new ColumnDefinition("description", ColumnType.Text, required: true),
            new ColumnDefinition("cost", ColumnType.Decimal)
        ]));

    // ---- TR-4: the audit record is atomic with the data -----------------------------------------------

    /// <summary>An injected failure between the data write and the change write leaves neither.</summary>
    [Fact]
    public void A_failure_between_the_data_write_and_the_change_write_leaves_neither()
    {
        using var storage = Open();
        GivenExpenses(storage);
        var request = storage.Traces.Begin(Ulid.NewUlid(), "add an expense");
        Ulid? written = null;

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            written = storage.Create("expenses", new Dictionary<string, object?> { ["description"] = "tickets", ["cost"] = 12m }).Id;
            throw new InvalidOperationException("injected between the data write and the change write");
        }));

        Assert.NotNull(written);
        Assert.Null(storage.GetById("expenses", written.Value));
        Assert.Empty(storage.Traces.Changes(request.Id));
        Assert.Empty(storage.GetAll("expenses"));
    }

    /// <summary>And the other order: a change recorded first, then the data write failing, leaves no change either.</summary>
    [Fact]
    public void A_failure_between_the_change_write_and_the_data_write_leaves_neither()
    {
        using var storage = Open();
        GivenExpenses(storage);
        var request = storage.Traces.Begin(Ulid.NewUlid(), "add an expense");

        Assert.Throws<InvalidOperationException>(() => storage.InUnitOfWork(() =>
        {
            storage.Traces.Record(DataChanges.Insert(request.Id, "expenses", Ulid.NewUlid(), Ulid.NewUlid(), Reversibility.Reversible));
            throw new InvalidOperationException("injected after the change write");
        }));

        Assert.Empty(storage.Traces.Changes(request.Id));

        // And the storage is still usable for the next unit of work, which is what rollback means.
        var record = storage.Create("expenses", new Dictionary<string, object?> { ["description"] = "hotel" });
        Assert.NotNull(storage.GetById("expenses", record.Id));
    }

    // ---- TR-4a: lifecycle and steps are on disk as they happen -------------------------------------------

    /// <summary>
    /// A request killed while waiting for a confirmation has its state and its steps so far on
    /// disk: seen from a read-only connection while the writer is still open, and again after the
    /// writer is closed without finishing it.
    /// </summary>
    [Fact]
    public void A_request_killed_while_waiting_has_its_state_and_steps_on_disk()
    {
        Ulid request;
        var at = DateTimeOffset.UtcNow;

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "drop a field");
            request = began.Id;

            storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, "the person", StepStatus.Completed, at, at.AddSeconds(1)) { Input = "drop the notes" });
            storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, "what was meant", StepStatus.Completed, at.AddSeconds(1), at.AddSeconds(2)) { Output = "remove a field" });
            storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, "the question", StepStatus.Completed, at.AddSeconds(2), at.AddSeconds(3)) { Output = "14 records hold a value" });

            Assert.NotNull(lifecycle.Hold(began, new RequestIntent("remove-field", """{"collection":"conferences","field":"notes"}""", new string('b', Hashes.Length))));

            // Seen from outside, while the writer is still open: this is what a kill leaves.
            using (var observer = Observe())
            {
                var seen = observer.Traces.Read(request);
                Assert.NotNull(seen);
                Assert.Equal(RequestState.WaitingForUser, seen.Value.Request.State);
                Assert.Equal("remove-field", seen.Value.Request.Intent!.Kind);
                Assert.Equal(3, seen.Value.Steps.Count);
            }
        }

        // And after the writer went away without finishing the request.
        using (var storage = Open())
        {
            var read = storage.Traces.Read(request);
            Assert.NotNull(read);
            Assert.Equal(RequestState.WaitingForUser, read.Value.Request.State);
            Assert.Equal(["the person", "what was meant", "the question"], read.Value.Steps.Select(static step => step.Name));
            Assert.Equal("""{"collection":"conferences","field":"notes"}""", read.Value.Request.Intent!.Payload);
        }
    }

    /// <summary>A request that made three model calls and then failed has three model-call steps recorded.</summary>
    [Fact]
    public void A_request_that_failed_after_three_model_calls_has_three_steps_recorded()
    {
        Ulid request;

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "store some text");
            request = began.Id;
            var at = DateTimeOffset.UtcNow;

            for (var call = 0; call < 3; call++)
            {
                storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, $"call {call + 1}", StepStatus.Completed, at.AddSeconds(call), at.AddSeconds(call + 1))
                {
                    Call = ModelCalls.For("qwen3.5:4b", $"prompt {call}", 100 + call, 20, TimeSpan.FromSeconds(1))
                });
            }

            Assert.NotNull(lifecycle.Fail(began, "the model produced nothing usable after three attempts"));
        }

        using (var storage = Open())
        {
            var read = storage.Traces.Read(request)!.Value;
            Assert.Equal(RequestState.Failed, read.Request.State);
            Assert.Equal(3, read.Steps.Count(static step => step.Call is not null));
            Assert.Equal(100 + 101 + 102, read.Steps.Sum(static step => step.Call?.PromptTokens ?? 0));
        }
    }

    // ---- TR-4b: an interrupted step never reads as done ----------------------------------------------------

    /// <summary>
    /// The process is killed mid-step; on reopening, that step reads as interrupted rather than as
    /// done, and the request as failed with a reason of interrupted (AG-8b).
    /// </summary>
    [Fact]
    public void A_step_interrupted_by_a_kill_reads_as_interrupted_rather_than_as_done()
    {
        Ulid request;
        Ulid running;

        using (var storage = Open())
        {
            var began = storage.Traces.Begin(Ulid.NewUlid(), "store a file");
            request = began.Id;
            var at = DateTimeOffset.UtcNow;

            storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, "what is in the file", StepStatus.Completed, at, at.AddSeconds(1)));
            running = Ulid.NewUlid();
            storage.Traces.Record(new ExecutionStep(running, request, "where it belongs", StepStatus.Running, at.AddSeconds(1)));
            // The kill: nothing else happens to the request.
        }

        using (var storage = Open())
        {
            var before = storage.Traces.Read(request)!.Value;
            Assert.Equal(StepStatus.Running, before.Steps.Single(step => step.Id == running).Status);

            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));

            Assert.Equal(RecoveryAction.FailedAsInterrupted, recovered.Action);
            Assert.Equal(1, recovered.StepsInterrupted);

            var after = storage.Traces.Read(request)!.Value;
            var step = after.Steps.Single(candidate => candidate.Id == running);

            Assert.Equal(StepStatus.Interrupted, step.Status);
            Assert.Null(step.EndedAt);
            Assert.Equal(StepStatus.Completed, after.Steps.Single(candidate => candidate.Id != running).Status);
            Assert.Equal(RequestState.Failed, after.Request.State);
            Assert.Equal(StartupRecovery.InterruptedReason, after.Request.Reason);
        }

        // Idempotent: a second recovery finds nothing unfinished.
        using (var storage = Open())
        {
            Assert.Empty(StartupRecovery.Run(storage.Traces));
        }
    }

    // ---- TR-4c: the durability point, stated ---------------------------------------------------------------

    /// <summary>The bound is a configured value with a default, and the default writes every step as it happens.</summary>
    [Fact]
    public void The_durability_point_is_configured_and_defaults_to_immediate()
    {
        Assert.True(TraceDurability.Default.IsImmediate);
        Assert.Equal(TimeSpan.Zero, TraceDurability.Default.DiagnosticDelay);
        Assert.False(new TraceDurability(TimeSpan.FromMilliseconds(250)).IsImmediate);
    }

    /// <summary>A lifecycle transition is on disk before the call that follows it begins.</summary>
    [Fact]
    public void A_lifecycle_transition_is_on_disk_before_the_next_call_begins()
    {
        using var storage = Open(new TraceDurability(TimeSpan.FromHours(1)));
        var lifecycle = new RequestLifecycle(storage.Traces);

        var request = lifecycle.Begin(Ulid.NewUlid(), "delete a record");

        using (var observer = Observe())
        {
            Assert.Equal(RequestState.Running, observer.Traces.Request(request.Id)!.State);
        }

        var waiting = lifecycle.Hold(request, new RequestIntent("delete", "{}", new string('c', Hashes.Length)))!;

        using (var observer = Observe())
        {
            var seen = observer.Traces.Request(request.Id)!;
            Assert.Equal(RequestState.WaitingForUser, seen.State);
            Assert.Equal(1, seen.Transitions);
        }

        Assert.NotNull(lifecycle.Claim(waiting));

        using (var observer = Observe())
        {
            Assert.Equal(RequestState.Resuming, observer.Traces.Request(request.Id)!.State);
        }
    }

    /// <summary>
    /// With a delay configured, a diagnostic step is held - and written at the latest at the next
    /// lifecycle transition, so that the steps that led to a state are on disk with it.
    /// </summary>
    [Fact]
    public void A_held_diagnostic_step_is_written_at_the_next_transition()
    {
        using var storage = Open(new TraceDurability(TimeSpan.FromHours(1)));
        var lifecycle = new RequestLifecycle(storage.Traces);
        var request = lifecycle.Begin(Ulid.NewUlid(), "store some text");
        var at = DateTimeOffset.UtcNow;

        storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request.Id, "what was meant", StepStatus.Completed, at, at.AddSeconds(1)));

        using (var observer = Observe())
        {
            // Held: not on disk yet, and this is the step TR-4c says a crash may lose.
            Assert.Empty(observer.Traces.Read(request.Id)!.Value.Steps);
        }

        Assert.NotNull(lifecycle.Complete(request));

        using (var observer = Observe())
        {
            var seen = observer.Traces.Read(request.Id)!.Value;
            Assert.Equal(RequestState.Completed, seen.Request.State);
            Assert.Single(seen.Steps);
        }
    }

    /// <summary>A close is not a crash: whatever is still held is written when the storage is disposed.</summary>
    [Fact]
    public void A_held_diagnostic_step_is_written_when_the_storage_closes()
    {
        Ulid request;

        using (var storage = Open(new TraceDurability(TimeSpan.FromHours(1))))
        {
            request = storage.Traces.Begin(Ulid.NewUlid(), "store some text").Id;
            var at = DateTimeOffset.UtcNow;
            storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request, "what was meant", StepStatus.Completed, at, at.AddSeconds(1)));
        }

        using (var storage = Open())
        {
            Assert.Single(storage.Traces.Read(request)!.Value.Steps);
        }
    }

    /// <summary>A step held longer than the delay is written by the next step recorded.</summary>
    [Fact]
    public void A_step_held_past_the_delay_is_written_by_the_next_recording()
    {
        using var storage = Open(new TraceDurability(TimeSpan.FromMilliseconds(20)));
        var request = storage.Traces.Begin(Ulid.NewUlid(), "store some text");
        var at = DateTimeOffset.UtcNow;

        storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request.Id, "first", StepStatus.Completed, at, at.AddSeconds(1)));
        Thread.Sleep(60);
        storage.Traces.Record(new ExecutionStep(Ulid.NewUlid(), request.Id, "second", StepStatus.Completed, at, at.AddSeconds(2)));

        using var observer = Observe();
        Assert.Equal(2, observer.Traces.Read(request.Id)!.Value.Steps.Count);
    }
}
