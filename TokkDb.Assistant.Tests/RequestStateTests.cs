using TokkDb.Assistant.Agents.Requests;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The request as a resumable state machine (D-15, AG-8, AG-8a, AG-8b, step 3.4): the
/// single-writer lock at open, every transition a compare-and-swap, the held intent surviving a
/// restart, the content hash as the idempotency key, and the three recovery paths.
/// </summary>
public sealed class RequestStateTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("request-state");

    public void Dispose() => _database.Dispose();

    private TokkDbStorage Open() => new(_database.FilePath);

    private static RequestIntent Intent(string kind = "placement", char fill = 'a') =>
        new(kind, """{"target":"conferences","rows":2}""", new string(fill, Hashes.Length));

    // ---- The lock ---------------------------------------------------------------------------------

    /// <summary>Opening the same database twice reports it is in use rather than corrupting it (AG-8a).</summary>
    [Fact]
    public void Opening_the_same_database_twice_reports_it_is_in_use()
    {
        using var first = Open();

        var refused = Assert.Throws<StorageInUseException>(() => new TokkDbStorage(_database.FilePath));

        Assert.Equal(_database.FilePath, refused.Location);
        Assert.IsAssignableFrom<StorageException>(refused);

        // And once the first is closed, the second opens.
        first.Dispose();
        using var second = Open();
        Assert.Empty(second.GetCollectionDefinitions());
    }

    // ---- The held intent ---------------------------------------------------------------------------

    /// <summary>
    /// The process is killed while a confirmation is pending; on reopening the question is still
    /// there, with the validated intent and its hash exactly as they were, and answering it claims
    /// the request once.
    /// </summary>
    [Fact]
    public void A_pending_confirmation_survives_a_restart_and_is_answered_once()
    {
        Ulid request;
        var intent = Intent();

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "store a file");
            request = began.Id;
            Assert.NotNull(lifecycle.Hold(began, intent));
        }

        using (var storage = Open())
        {
            Assert.Empty(StartupRecovery.Run(storage.Traces).Where(static recovered => recovered.Action is not RecoveryAction.RestoredWaiting));

            var lifecycle = new RequestLifecycle(storage.Traces);
            var waiting = storage.Traces.Request(request)!;

            Assert.Equal(RequestState.WaitingForUser, waiting.State);
            Assert.Equal(intent, waiting.Intent);

            var claimed = lifecycle.Claim(waiting);
            Assert.NotNull(claimed);
            Assert.Equal(RequestState.Resuming, claimed.State);
            Assert.Equal(intent, claimed.Intent);

            // The same answer submitted again arrives with the same expectation and loses.
            Assert.Null(lifecycle.Claim(waiting));

            var done = lifecycle.Complete(claimed, committedHash: intent.Hash);
            Assert.NotNull(done);
            Assert.True(done.HasCommitted(intent.Hash));
        }
    }

    /// <summary>
    /// A double-submitted confirmation produces one claim, asserted by firing the transition
    /// concurrently (AG-8a).
    /// </summary>
    [Fact]
    public void A_double_submitted_confirmation_moves_the_request_once()
    {
        using var storage = Open();
        var lifecycle = new RequestLifecycle(storage.Traces);
        var began = lifecycle.Begin(Ulid.NewUlid(), "delete some records");
        var waiting = lifecycle.Hold(began, Intent("delete"))!;

        var outcomes = new RequestTrace?[8];
        var go = new ManualResetEventSlim();

        var threads = Enumerable.Range(0, outcomes.Length).Select(index => new Thread(() =>
        {
            go.Wait();
            outcomes[index] = lifecycle.Claim(waiting);
        })).ToList();

        foreach (var thread in threads) thread.Start();
        go.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(1, outcomes.Count(static outcome => outcome is not null));
        Assert.Equal(2, storage.Traces.Request(began.Id)!.Transitions);
    }

    /// <summary>
    /// Replaying a resolved action whose hash is already recorded as committed is a no-op: the
    /// hash is the idempotency key (AG-8a), and it is AG-3d's content hash, so what committed is
    /// provably what was shown.
    /// </summary>
    [Fact]
    public void A_committed_action_is_recognised_by_its_hash()
    {
        using var storage = Open();
        var lifecycle = new RequestLifecycle(storage.Traces);
        var intent = Intent();
        var began = lifecycle.Begin(Ulid.NewUlid(), "store a file");
        var waiting = lifecycle.Hold(began, intent)!;
        var claimed = lifecycle.Claim(waiting)!;

        Assert.False(claimed.HasCommitted(intent.Hash));

        var committed = lifecycle.Committed(claimed, intent.Hash)!;

        Assert.True(committed.HasCommitted(intent.Hash));
        Assert.False(committed.HasCommitted(new string('z', Hashes.Length)));
        Assert.Equal(RequestState.Resuming, committed.State);

        // And it survives a reopen, which is where a replay would come from.
        var reopened = storage.Traces.Request(began.Id)!;
        Assert.True(reopened.HasCommitted(intent.Hash));
    }

    /// <summary>A move the state machine does not allow is refused before the recorder is asked.</summary>
    [Fact]
    public void A_move_the_state_machine_does_not_allow_is_refused()
    {
        using var storage = Open();
        var lifecycle = new RequestLifecycle(storage.Traces);
        var began = lifecycle.Begin(Ulid.NewUlid(), "store a file");

        // Running cannot be claimed: there is no answer to claim it with.
        Assert.Throws<InvalidOperationException>(() => lifecycle.Claim(began));

        var done = lifecycle.Complete(began)!;
        Assert.Throws<InvalidOperationException>(() => lifecycle.Hold(done, Intent()));
        Assert.Throws<InvalidOperationException>(() => lifecycle.Fail(done, "too late"));

        Assert.True(RequestLifecycle.Allows(RequestState.WaitingForUser, RequestState.Resuming));
        Assert.True(RequestLifecycle.Allows(RequestState.Resuming, RequestState.WaitingForUser));
        Assert.False(RequestLifecycle.Allows(RequestState.Completed, RequestState.Running));
    }

    /// <summary>The answer was no: nothing happens, the request completes, and the refusal is in the trace (N-8).</summary>
    [Fact]
    public void Declining_a_confirmation_completes_the_request_with_the_refusal_recorded()
    {
        using var storage = Open();
        var lifecycle = new RequestLifecycle(storage.Traces);
        var began = lifecycle.Begin(Ulid.NewUlid(), "drop a field");
        var waiting = lifecycle.Hold(began, Intent("remove-field"))!;

        var declined = lifecycle.Decline(waiting)!;

        Assert.Equal(RequestState.Completed, declined.State);
        Assert.Equal("declined by the user", declined.Reason);
        Assert.Null(declined.CommittedHash);
        Assert.Empty(storage.Traces.Changes(began.Id));
    }

    // ---- The three recovery paths (AG-8b) --------------------------------------------------------------------

    /// <summary>Killed while Running with nothing committed: Failed, with a reason of interrupted.</summary>
    [Fact]
    public void Recovery_fails_a_running_request_whose_mutation_did_not_commit()
    {
        Ulid request;
        using (var storage = Open())
        {
            request = storage.Traces.Begin(Ulid.NewUlid(), "store a file").Id;
        }

        using (var storage = Open())
        {
            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));
            Assert.Equal(RecoveryAction.FailedAsInterrupted, recovered.Action);
            Assert.Equal(RequestState.Failed, recovered.After.State);
            Assert.Equal(StartupRecovery.InterruptedReason, recovered.After.Reason);
            Assert.False(recovered.NeedsResumption);
        }
    }

    /// <summary>Killed while Running after the mutation committed: completed from the record, and nothing is re-executed.</summary>
    [Fact]
    public void Recovery_completes_a_running_request_whose_mutation_committed()
    {
        Ulid request;
        Ulid record;

        using (var storage = Open())
        {
            storage.CreateCollection(new CollectionDefinition("expenses", columns: [new ColumnDefinition("description", ColumnType.Text, required: true)]));
            request = storage.Traces.Begin(Ulid.NewUlid(), "add an expense").Id;

            record = storage.InUnitOfWork(() =>
            {
                var written = storage.Create("expenses", new Dictionary<string, object?> { ["description"] = "tickets" });
                storage.Traces.Record(DataChanges.Insert(request, "expenses", written.Id,
                    storage.HeadVersion("expenses", written.Id)!.Value, Reversibility.Reversible));
                return written.Id;
            });
            // The kill: the reply was never written.
        }

        using (var storage = Open())
        {
            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));

            Assert.Equal(RecoveryAction.CompletedFromRecord, recovered.Action);
            Assert.Equal(RequestState.Completed, recovered.After.State);
            Assert.Equal(StartupRecovery.CompletedFromRecordReason, recovered.After.Reason);
            Assert.False(recovered.NeedsResumption);

            // The write is there once, and nothing re-executed it.
            Assert.Single(storage.GetAll("expenses"));
            Assert.Single(storage.Traces.Changes(request));
            Assert.NotNull(storage.GetById("expenses", record));
        }
    }

    /// <summary>Killed while WaitingForUser: restored exactly as it was, question and all.</summary>
    [Fact]
    public void Recovery_restores_a_waiting_request_as_it_was()
    {
        Ulid request;
        var intent = Intent("remove-field", 'd');

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "drop a field");
            request = began.Id;
            lifecycle.Hold(began, intent);
        }

        using (var storage = Open())
        {
            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));

            Assert.Equal(RecoveryAction.RestoredWaiting, recovered.Action);
            Assert.Equal(recovered.Before, recovered.After);
            Assert.Equal(RequestState.WaitingForUser, recovered.After.State);
            Assert.Equal(intent, recovered.After.Intent);
            Assert.Equal(1, recovered.After.Transitions);
        }
    }

    /// <summary>Killed while Resuming before the action committed: claimed atomically, and handed back for the redo.</summary>
    [Fact]
    public void Recovery_claims_a_resuming_request_before_its_work_is_redone()
    {
        Ulid request;
        var intent = Intent();

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "store a file");
            request = began.Id;
            lifecycle.Claim(lifecycle.Hold(began, intent)!);
            // The kill, mid-redo, before anything committed.
        }

        using (var storage = Open())
        {
            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));

            Assert.Equal(RecoveryAction.ClaimedForResumption, recovered.Action);
            Assert.True(recovered.NeedsResumption);
            Assert.Equal(RequestState.Resuming, recovered.After.State);
            Assert.Equal(recovered.Before.Transitions + 1, recovered.After.Transitions);
            Assert.Equal(intent, recovered.After.Intent);

            // The claim is the compare-and-swap: a second recovery with the old expectation loses.
            Assert.Null(storage.Traces.Move(request, RequestState.Resuming, recovered.Before.Transitions));
        }
    }

    /// <summary>Killed while Resuming after the action committed: completed, and nothing is redone (AG-8a).</summary>
    [Fact]
    public void Recovery_completes_a_resuming_request_whose_action_had_committed()
    {
        Ulid request;
        var intent = Intent();

        using (var storage = Open())
        {
            var lifecycle = new RequestLifecycle(storage.Traces);
            var began = lifecycle.Begin(Ulid.NewUlid(), "store a file");
            request = began.Id;
            var claimed = lifecycle.Claim(lifecycle.Hold(began, intent)!)!;
            lifecycle.Committed(claimed, intent.Hash);
            // The kill, after the write and before the reply.
        }

        using (var storage = Open())
        {
            var recovered = Assert.Single(StartupRecovery.Run(storage.Traces));

            Assert.Equal(RecoveryAction.CompletedAlreadyCommitted, recovered.Action);
            Assert.False(recovered.NeedsResumption);
            Assert.Equal(RequestState.Completed, recovered.After.State);
            Assert.True(recovered.After.HasCommitted(intent.Hash));
        }
    }
}
