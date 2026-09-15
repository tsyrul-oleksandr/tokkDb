using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Taking a request back (N-11, AG-11 to AG-11f, D-17): the record changes through version
/// history in two passes inside one unit of work, the structural changes from the journal's
/// inverse at their position, all replayed in exact reverse order. Refused whole, naming every
/// blocker, when anything blocks; the rest offered as a second, explicitly chosen action on a
/// card of its own (AG-11b).
/// </summary>
internal static class UndoFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, string message)
    {
        var target = Target(ctx);
        if (target is null)
        {
            return ctx.Complete("There is nothing recent to take back in this conversation.");
        }

        var changes = ctx.Recorder.Changes(target.Id);
        var records = changes.Where(static change => change.IsRecordChange).ToList();
        var structural = changes.Where(static change => !change.IsRecordChange).ToList();

        var blockers = Compensation.Validate(ctx.Storage, records);
        var irreversible = structural.Where(static change => change.Reversibility is Reversibility.NotReversible).ToList();

        ctx.Tracer.Note("what would be taken back", input: target.Operation,
            output: $"{records.Count} record changes, {structural.Count} changes to the shape; {blockers.Count} blocked");

        if (blockers.Count == 0 && irreversible.Count == 0)
        {
            return await ExecuteAsync(ctx, target, changes, records, $"undo:{target.Id}").ConfigureAwait(false);
        }

        // Refused whole, every blocker named (AG-11b), and the rest validated as an undo of its own.
        var lines = new List<string>();
        foreach (var blocker in blockers) lines.Add(Describe(ctx, blocker));
        foreach (var change in irreversible) lines.Add($"{Change(change)} cannot be undone: what it removed was not kept.");

        var blocked = blockers.Select(static blocker => blocker.Record).ToHashSet();
        var rest = records.Where(change => !blocked.Contains(new RecordReference(change.CollectionName, change.RecordId!.Value))).ToList();
        var restBlockers = rest.Count == 0 ? [] : Compensation.Validate(ctx.Storage, rest);

        var refusal = "That cannot be taken back as a whole: " + string.Join(" ", lines);

        if (rest.Count == 0 || irreversible.Count > 0 || restBlockers.Count > 0)
        {
            if (restBlockers.Count > 0)
            {
                refusal += " Leaving those out would not work either: " + string.Join(" ", restBlockers.Select(blocker => Describe(ctx, blocker)));
            }

            return ctx.Complete(refusal);
        }

        var thing = Replies.Plain(rest[0].CollectionName);
        var card = new ConfirmationCard(
            $"Put back the other {rest.Count} and leave {blockers.Count} as they are?",
            [refusal, $"{rest.Count} of {thing} would go back to how they were before.", $"{blockers.Count} would stay as they are now: {string.Join(", ", blockers.Select(blocker => Title(ctx, blocker.Record)))}."],
            [.. blockers.Take(ChangeClassifier.ExamplesShown).Select(blocker => Title(ctx, blocker.Record))],
            CanBeUndone: true,
            "This can be undone as long as nothing else changes these.",
            Yes: $"Put back the {rest.Count}",
            No: "Leave everything as it is");

        return ctx.Ask(card, IntentPayloads.PartialUndo(target.Id, [.. rest.Select(static change => change.Id)], [.. blockers.Select(blocker => Title(ctx, blocker.Record))]));
    }

    /// <summary>The answer to the partial offer was yes (AG-11b): the subset, validated again and replayed.</summary>
    public static async Task<TurnOutcome> ResumeAsync(TurnContext ctx)
    {
        var intent = ctx.Request.Intent ?? throw new InvalidOperationException("The request holds nothing to resume.");
        if (ctx.Request.HasCommitted(intent.Hash)) return ctx.Complete(Replies.AlreadyDone());

        if (!IntentPayloads.IsIntact(intent))
        {
            return ctx.Fail("what was about to be applied is not what you were shown, so it was not applied");
        }

        var (requestId, include, leftOut) = IntentPayloads.ReadPartialUndo(intent.Payload);
        var target = ctx.Recorder.Request(requestId)
                     ?? (ctx.Recorder.Changes(requestId) is { Count: > 0 } journaled
                         ? new RequestTrace(requestId, ctx.Conversation.Id, "a request whose diagnostics are no longer kept", RequestState.Completed, journaled[0].At, journaled[^1].At)
                         : null);
        var included = include.ToHashSet();
        var changes = ctx.Recorder.Changes(requestId).Where(change => included.Contains(change.Id)).ToList();

        var blockers = Compensation.Validate(ctx.Storage, changes);
        if (blockers.Count > 0)
        {
            return ctx.Complete("That cannot be taken back any more: " + string.Join(" ", blockers.Select(blocker => Describe(ctx, blocker))));
        }

        var outcome = await ExecuteAsync(ctx, target, changes, changes, intent.Hash, leftOut).ConfigureAwait(false);
        return outcome;
    }

    private static async Task<TurnOutcome> ExecuteAsync(TurnContext ctx, RequestTrace? target, IReadOnlyList<DataChange> all, IReadOnlyList<DataChange> records, string hash, IReadOnlyList<string>? leftOut = null)
    {
        using var lease = await ctx.Gate.EnterAsync("taking a request back", ctx.Cancellation).ConfigureAwait(false);
        ctx.Cancellation.ThrowIfCancellationRequested();

        var step = ctx.Tracer.Step("putting things back", input: target?.Operation);
        var touched = new Dictionary<RecordReference, Ulid?>();
        var restored = 0;

        try
        {
            ctx.Storage.InUnitOfWork(() =>
            {
                foreach (var change in records)
                {
                    var reference = new RecordReference(change.CollectionName, change.RecordId!.Value);
                    if (!touched.ContainsKey(reference)) touched[reference] = ctx.Storage.HeadVersion(reference.CollectionName, reference.Id);
                }

                // Record changes through history, in exact reverse order (AG-11a, AG-11f) ...
                var included = records.Select(static change => change.Id).ToHashSet();
                Compensation.Undo(ctx.Storage, records, change => included.Contains(change.Id));

                // ... and structural changes from the journal's inverse, at their position after them.
                foreach (var change in all.Where(static change => !change.IsRecordChange).Reverse())
                {
                    Invert(ctx, change, step.Id);
                }

                foreach (var (reference, before) in touched)
                {
                    var after = ctx.Storage.HeadVersion(reference.CollectionName, reference.Id);
                    if (after == before || after is null) continue;

                    restored++;
                    if (ctx.Storage.GetById(reference.CollectionName, reference.Id) is null)
                    {
                        ctx.Journal.Deleted(ctx.Request.Id, reference.CollectionName, reference.Id, before!.Value, step.Id);
                    }
                    else if (before is { } previous)
                    {
                        ctx.Journal.Updated(ctx.Request.Id, reference.CollectionName, reference.Id, previous, step.Id, "put back");
                    }
                    else
                    {
                        ctx.Journal.Inserted(ctx.Request.Id, reference.CollectionName, reference.Id, step.Id, "put back");
                    }
                }
            });
        }
        catch (CompensationRefusedException refused)
        {
            step.Failed(refused.Message);
            return ctx.Complete("That could not be taken back: " + string.Join(" ", refused.Blockers.Select(blocker => Describe(ctx, blocker))) + " Nothing was changed.");
        }
        catch (StorageException failure)
        {
            step.Failed(failure.Message);
            return ctx.Fail(failure.Message);
        }

        step.Done($"{restored} records put back");

        var thing = records.Count > 0 ? records[0].CollectionName : all.Count > 0 ? all[0].CollectionName : "things";
        var reply = restored == 0 && all.Any(static change => !change.IsRecordChange)
            ? "Put the shape back as it was."
            : Replies.Undone(restored, thing);
        if (leftOut is { Count: > 0 }) reply += $" Left as they are: {string.Join(", ", leftOut)}.";

        return ctx.Complete(reply, committedHash: hash);
    }

    /// <summary>The inverse of a structural change, from what the journal kept (AG-11e, D-17).</summary>
    private static void Invert(TurnContext ctx, DataChange change, Ulid step)
    {
        var storage = ctx.Storage;

        switch (change.Kind)
        {
            case ChangeKind.FieldAdded:
                foreach (var field in change.Fields)
                {
                    if (storage.GetCollectionDefinition(change.CollectionName)?.Column(field.Name) is not null)
                    {
                        var values = ctx.Journal.RemovedValues(change.CollectionName, field.Name);
                        storage.RemoveColumn(change.CollectionName, field.Name);
                        ctx.Journal.Structural(ctx.Request.Id, ChangeKind.FieldRemoved, change.CollectionName, values, Reversibility.ReversibleWithConditions, step);
                    }
                }

                break;

            case ChangeKind.FieldRemoved:
            {
                var definition = storage.GetCollectionDefinition(change.CollectionName);
                if (definition is null) break;

                var kind = change.Fields.Select(static field => field.Before.Kind).FirstOrDefault(static kind => kind is not JournalKind.Nothing);
                var type = kind switch
                {
                    JournalKind.Integer => ColumnType.Integer,
                    JournalKind.Decimal => ColumnType.Decimal,
                    JournalKind.Boolean => ColumnType.Boolean,
                    JournalKind.Date => ColumnType.Date,
                    JournalKind.Timestamp => ColumnType.Timestamp,
                    _ => ColumnType.Text
                };

                // The journal named the field by its records; the field's own name is the step's input.
                var name = FieldNameOf(ctx, change) ?? "restored";
                if (definition.Column(name) is null) storage.AddColumn(change.CollectionName, new ColumnDefinition(name, type));

                foreach (var (recordId, value) in DataChanges.Restore(change))
                {
                    if (!Ulid.TryParse(recordId, out var id)) continue;
                    var record = storage.GetById(change.CollectionName, id);
                    if (record is null) continue;
                    storage.Update(record.With(name, value));
                }

                ctx.Journal.Structural(ctx.Request.Id, ChangeKind.FieldAdded, change.CollectionName,
                    [FieldChange.Added(name, JournalValue.Of(Context.SchemaDigest.Kind(type), PayloadLimits.Default))], Reversibility.Reversible, step);
                break;
            }

            case ChangeKind.CollectionAdded:
                if (storage.GetCollectionDefinition(change.CollectionName) is { } added && storage.GetAll(added.Name).Count == 0)
                {
                    storage.DeleteCollection(added.Name);
                    ctx.Journal.Structural(ctx.Request.Id, ChangeKind.CollectionRemoved, added.Name,
                        [.. added.Columns.Select(column => FieldChange.Removed(column.Name, JournalValue.Of(Context.SchemaDigest.Kind(column.Type), PayloadLimits.Default)))],
                        Reversibility.NotReversible, step);
                }

                break;

            case ChangeKind.FieldChanged:
                foreach (var field in change.Fields)
                {
                    if (field.Name == "renamed" && field.Before.Value is string from && field.After.Value is string to)
                    {
                        storage.RenameColumn(change.CollectionName, to, from);
                    }
                    else if (field.Before.Value is string before && field.After.Value is string after && (after is "no two the same" or "may repeat"))
                    {
                        storage.SetUnique(change.CollectionName, field.Name, before == "no two the same");
                    }
                    else if (field.Name == "display rule")
                    {
                        storage.SetDisplayRule(change.CollectionName, field.Before.Value is string template ? new DisplayRule(template) : null);
                    }
                    else if (Context.SchemaDigest.Type(field.Before.Value as string) is { } type)
                    {
                        storage.RetypeColumn(change.CollectionName, field.Name, type);
                    }
                }

                ctx.Journal.Structural(ctx.Request.Id, ChangeKind.FieldChanged, change.CollectionName,
                    [.. change.Fields.Select(static field => new FieldChange(field.Name, field.After, field.Before))], Reversibility.Reversible, step);
                break;

            case ChangeKind.RelationChanged:
                foreach (var field in change.Fields.Where(static field => field.After.Kind is not JournalKind.Nothing))
                {
                    storage.RemoveRelation(field.Name);
                }

                break;

            case ChangeKind.CollectionRemoved:
                throw new CompensationRefusedException([new Blocker(new RecordReference(change.CollectionName, Ulid.Empty), "a thing that was removed cannot be put back")], atReplay: true);
        }
    }

    private static string? FieldNameOf(TurnContext ctx, DataChange change)
    {
        if (change.StepId is not { } stepId) return null;
        var read = ctx.Recorder.Read(change.RequestId);
        var step = read?.Steps.FirstOrDefault(step => step.Id == stepId);
        var input = step?.Input;
        if (input is null) return null;

        // "drop the notes from conferences"
        var words = input.Split(' ');
        var at = Array.IndexOf(words, "the");
        return at >= 0 && at + 1 < words.Length ? words[at + 1] : null;
    }

    /// <summary>
    /// The last request in this conversation that changed something and is not itself an undo.
    /// A request whose diagnostics have been purged (TR-7) is still a target: the change journal
    /// is on its own, longer window (TR-8), and what the person said is in the conversation, so
    /// the request is stood in for from those - the compensation window is measured from its
    /// last change, which is when it finished changing things.
    /// </summary>
    private static RequestTrace? Target(TurnContext ctx)
    {
        foreach (var turn in ctx.Storage.Conversations.Turns(ctx.Conversation.Id).Reverse())
        {
            if (turn.RequestId is not { } id || id == ctx.Request.Id) continue;
            var changes = ctx.Recorder.Changes(id);
            if (changes.Count == 0) continue;
            var request = ctx.Recorder.Request(id);
            if (request is not null)
            {
                if (!request.IsFinished || request.Operation.StartsWith("undo", StringComparison.OrdinalIgnoreCase)) continue;
                return request;
            }

            if (turn.Speaker is TurnSpeaker.Person && turn.Text.StartsWith("undo", StringComparison.OrdinalIgnoreCase)) continue;
            var last = changes[^1].At;
            return new RequestTrace(id, ctx.Conversation.Id, turn.Speaker is TurnSpeaker.Person && turn.Text.Length > 0 ? turn.Text : "a request whose diagnostics are no longer kept", RequestState.Completed, changes[0].At, last);
        }

        return null;
    }

    private static string Describe(TurnContext ctx, Blocker blocker)
    {
        var title = Title(ctx, blocker.Record);
        var reason = blocker.Reason.StartsWith("touched since", StringComparison.Ordinal) ? "has been changed since"
            : blocker.Reason.Contains("no longer kept", StringComparison.Ordinal) ? "is no longer kept as it was"
            : blocker.Reason;
        return blocker.Other is { } other ? $"{title} {reason} ({Title(ctx, other)})." : $"{title} {reason}.";
    }

    private static string Title(TurnContext ctx, RecordReference reference)
    {
        var definition = ctx.Storage.GetCollectionDefinition(reference.CollectionName);
        var record = definition is null ? null : ctx.Storage.GetById(reference.CollectionName, reference.Id);
        return record is null ? $"one of {Replies.Plain(reference.CollectionName)} ({DisplayValue.Shortened(reference.Id)})" : DisplayValue.For(definition!, record);
    }

    private static string Change(DataChange change) => change.Kind switch
    {
        ChangeKind.FieldRemoved => $"dropping a field from {Replies.Plain(change.CollectionName)}",
        ChangeKind.CollectionRemoved => $"removing {Replies.Plain(change.CollectionName)}",
        _ => $"a change to {Replies.Plain(change.CollectionName)}"
    };
}
