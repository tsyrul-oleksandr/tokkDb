using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Changing what a thing keeps, and removing records (S-6, N-8, D-7, D-14, AG-4, BR-8): the change
/// is classified by what it can do, the evidence is computed before the question is put, a Safe
/// change happens, and the rest wait for a yes on a card that states the loss in counts and
/// examples. The browser's own edits come through <see cref="ApplyAsync"/> the same way.
/// </summary>
internal static class RestructureFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, string message)
    {
        var definitions = ctx.Storage.GetCollectionDefinitions();
        if (definitions.Count == 0)
        {
            return ctx.Complete("Nothing is stored yet, so there is nothing to change.");
        }

        var result = await ctx.RunAsync(Catalogue.Structure, "message: " + message, EgressClass.SchemaDigest, Answers.Structure(definitions), summary: message).ConfigureAwait(false);
        var answer = result.Value;

        if (answer.Action == "none")
        {
            return ctx.Complete("I could not see what to change from that. Say which thing and which of its fields, and what should happen to it.");
        }

        if (answer.Action == "set-required")
        {
            return ctx.Complete("Making a field always needed is not something that can be changed yet.");
        }

        var action = ToAction(answer);
        return await ApplyAsync(ctx, action).ConfigureAwait(false);
    }

    /// <summary>Removing records: the ones shown, pointed at by position or by name (N-8, BR-8).</summary>
    public static Task<TurnOutcome> RemoveAsync(TurnContext ctx, string message)
    {
        if (ctx.LastHandle is not { } handle)
        {
            return Task.FromResult(ctx.Complete("Say which ones: ask for them first, so that I can see which you mean."));
        }

        var chosen = Pointed(ctx, handle, message);
        if (chosen.Count == 0)
        {
            return Task.FromResult(ctx.Complete($"None of the {Replies.Plain(handle.Thing)} you were shown matches that. Say which one by its name, or by its number in the list."));
        }

        // "For good", "erase", "completely": not a removal that can be put back, but an erasure (NF-4d).
        return ApplyAsync(ctx, Intents.AsksForGood(message) ? new EraseRecords(handle.Thing, chosen) : new DeleteRecords(handle.Thing, chosen));
    }

    /// <summary>
    /// One classified change, from the conversation or the browser (BR-8): applied now when it is
    /// Safe, asked about otherwise, and either way recorded as a trace that appears in the diagram.
    /// </summary>
    public static async Task<TurnOutcome> ApplyAsync(TurnContext ctx, StructuralAction action)
    {
        ClassifiedChange classified;
        try
        {
            classified = ctx.Classifier.Classify(action);
        }
        catch (StorageException failure)
        {
            return ctx.Fail(failure.Message);
        }

        ctx.Tracer.Note("what it would do", input: action.Describe(),
            output: $"{classified.Class}, {classified.Reversibility}: {classified.Evidence.Sentence}");

        if (classified.NeedsConfirmation)
        {
            return ctx.Ask(ConfirmationCard.For([classified], ctx.Options.Retention.Compensation), IntentPayloads.Structure(action, SchemaVersion.Of(ctx.Storage)));
        }

        return await ExecuteAsync(ctx, classified).ConfigureAwait(false);
    }

    /// <summary>The answer was yes: the same action, re-classified, unless the shape moved since (AG-9) or the intent was substituted (AG-3d).</summary>
    public static async Task<TurnOutcome> ResumeAsync(TurnContext ctx)
    {
        var intent = ctx.Request.Intent ?? throw new InvalidOperationException("The request holds nothing to resume.");

        if (ctx.Request.HasCommitted(intent.Hash)) return ctx.Complete(Replies.AlreadyDone());

        if (!IntentPayloads.IsIntact(intent))
        {
            ctx.Tracer.Note("the check", output: "the change differs from the one that was confirmed; refused");
            return ctx.Fail("what was about to be applied is not what you were shown, so it was not applied");
        }

        var (action, schemaThen) = IntentPayloads.ReadStructure(intent.Payload);

        ClassifiedChange classified;
        try
        {
            classified = ctx.Classifier.Classify(action);
        }
        catch (StorageException failure)
        {
            return ctx.Fail("what was agreed no longer fits what is stored: " + failure.Message);
        }

        if (!string.Equals(SchemaVersion.Of(ctx.Storage), schemaThen, StringComparison.Ordinal))
        {
            ctx.Tracer.Note("re-planned", input: "the storage changed since the question was asked", output: classified.Evidence.Sentence);
            return ctx.Ask(ConfirmationCard.For([classified], ctx.Options.Retention.Compensation, "Things changed since you were asked. " + classified.Description + "?"),
                IntentPayloads.Structure(action, SchemaVersion.Of(ctx.Storage)));
        }

        return await ExecuteAsync(ctx, classified, intent.Hash).ConfigureAwait(false);
    }

    private static async Task<TurnOutcome> ExecuteAsync(TurnContext ctx, ClassifiedChange classified, string? hash = null)
    {
        using var lease = await ctx.Gate.EnterAsync(classified.Description, ctx.Cancellation).ConfigureAwait(false);
        ctx.Cancellation.ThrowIfCancellationRequested();

        var step = ctx.Tracer.Step("the change", input: classified.Description);

        try
        {
            ctx.Storage.InUnitOfWork(() => StructuralExecutor.Apply(ctx, classified, step.Id));
        }
        catch (Exception failure) when (failure is StorageException or NotSupportedException)
        {
            step.Failed(failure.Message);
            return ctx.Fail(failure.Message);
        }

        step.Done(classified.Evidence.Sentence);

        // An erasure clears the payloads of every request that named the record, this one
        // included (NF-4d1): what was asked and what would go named the record, and the step
        // just finished quoted its title. The conversation keeps what the person said.
        if (classified.Action is EraseRecords erased)
        {
            foreach (var id in erased.Ids) ctx.Recorder.ClearPayloadsNaming(id);
        }

        var reply = classified.Action switch
        {
            DeleteRecords delete => delete.Ids.Count == 1 ? $"Removed one of {Replies.Plain(delete.Thing)}." : $"Removed {delete.Ids.Count} of {Replies.Plain(delete.Thing)}.",
            EraseRecords erase => (erase.Ids.Count == 1 ? $"Erased one of {Replies.Plain(erase.Thing)} for good." : $"Erased {erase.Ids.Count} of {Replies.Plain(erase.Thing)} for good.") + " What you said about it in conversations is kept.",
            RemoveField remove => $"Dropped the {Replies.Plain(remove.Field)} from {Replies.Plain(remove.Thing)}." + (classified.Evidence.Count > 0 ? $" {classified.Evidence.Count} values went with it." : ""),
            ChangeRecord change when classified.Evidence.Examples.Count > 0 => $"Changed {Replies.Plain(change.Field)} of {classified.Evidence.Examples[0]} to {Values.Show(change.Value)}.",
            ChangeRecord => "Nothing to change: it is no longer there.",
            _ => Capitalise(classified.Description) + ". Done."
        };

        return ctx.Complete(reply, committedHash: hash);
    }

    private static StructuralAction ToAction(StructureAnswer answer) => answer.Action switch
    {
        "remove-field" => new RemoveField(answer.Thing, answer.Field),
        "rename-field" => new RenameField(answer.Thing, answer.Field, Placement.IncomingShape.AsThingName(answer.NewName.Length > 0 ? answer.NewName : answer.Field)),
        "retype-field" => new RetypeField(answer.Thing, answer.Field, SchemaDigest.Type(answer.Kind) ?? ColumnType.Text),
        "add-field" => new AddField(answer.Thing, new ColumnDefinition(Placement.IncomingShape.AsThingName(answer.Field.Length > 0 ? answer.Field : answer.NewName), SchemaDigest.Type(answer.Kind) ?? ColumnType.Text)),
        "set-unique" => new MakeUnique(answer.Thing, answer.Field),
        "remove-thing" => new RemoveThing(answer.Thing),
        _ => throw new NotSupportedException($"'{answer.Action}' is not a change that can be made.")
    };

    /// <summary>The shown records a message points at: by position, or by a word of their name.</summary>
    private static List<Ulid> Pointed(TurnContext ctx, QueryResultHandle handle, string message)
    {
        var words = message.ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', '\'', '"', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static word => word.Length > 2)
            .ToList();

        var ordinals = new Dictionary<string, int> { ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["last"] = handle.Shown.Count };
        var chosen = new List<Ulid>();

        foreach (var (word, index) in ordinals)
        {
            if (words.Contains(word) && index >= 1 && index <= handle.Shown.Count) chosen.Add(handle.Shown[index - 1].Id);
        }

        if (chosen.Count > 0) return chosen;

        foreach (var shown in handle.Shown)
        {
            var title = shown.Title.ToLowerInvariant();
            if (words.Any(word => title.Contains(word, StringComparison.Ordinal) && !Stop.Contains(word))) chosen.Add(shown.Id);
        }

        return chosen;
    }

    private static readonly HashSet<string> Stop = ["the", "one", "ones", "remove", "delete", "forget", "get", "rid", "that", "this", "those", "them", "and", "please", "from", "of"];

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
