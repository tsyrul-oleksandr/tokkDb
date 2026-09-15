using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Correcting a record by saying what is wrong (S-5, QR-4, QR-4a, QR-4b, step 9.1): the record is
/// resolved against the identities the handle recorded, never a re-run; staleness is checked at
/// the field being corrected and nowhere else; and the change is one write with its record, so
/// the trace shows the old value beside the new.
/// </summary>
internal static class CorrectionFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, string message)
    {
        if (ctx.LastHandle is not { } handle)
        {
            return ctx.Complete("Say which one you mean: ask for them first, so that I can see which you are correcting.");
        }

        var definition = ctx.Storage.GetCollectionDefinition(handle.Thing);
        if (definition is null || handle.Shown.Count == 0)
        {
            return ctx.Complete("The records you were shown are no longer there to correct.");
        }

        // The shown records, as they are now, numbered as they were shown (QR-4a).
        var lines = new List<string>();
        for (var index = 0; index < handle.Shown.Count; index++)
        {
            var shown = handle.Shown[index];
            var record = ctx.Storage.GetById(handle.Thing, shown.Id);
            var fields = record is null ? "(since removed)" : string.Join(", ", definition.Columns
                .Where(column => record[column.Name] is not null && !Names.Same(column.Name, Fingerprints.ColumnName))
                .Select(column => $"{column.Name}={Values.Show(record[column.Name])}"));
            lines.Add($"{index + 1}. {shown.Title}: {fields}");
        }

        var content = "records:\n" + string.Join("\n", lines) + "\nsaid: " + message;
        var result = await ctx.RunAsync(Catalogue.Correction, content, EgressClass.BoundedSample, Answers.Correction(handle.Shown.Count, definition), summary: message, withDigest: false).ConfigureAwait(false);
        var answer = result.Value;

        var target = handle.Shown[answer.Which - 1];
        var current = ctx.Storage.GetById(handle.Thing, target.Id);
        if (current is null)
        {
            return ctx.Complete($"{target.Title} has since been removed, so nothing was changed.");
        }

        var column = definition.Column(answer.Field)!;
        Values.TryRead(column.Type, answer.Value, out var value);

        // QR-4b: staleness at the field being corrected, not the record and not the re-run.
        var head = ctx.Storage.HeadVersion(handle.Thing, target.Id);
        if (head is { } now && now != target.Version && ctx.Storage.Keeps(handle.Thing, target.Id, target.Version))
        {
            var since = ctx.Storage.DiffVersions(handle.Thing, target.Id, target.Version, now);
            var changed = since.Changes.FirstOrDefault(change => Names.Same(change.ColumnName, column.Name));
            if (changed is not null)
            {
                ctx.Tracer.Note("the check", output: $"{column.Name} changed since it was shown: {Values.Show(changed.Before)} then, {Values.Show(changed.After)} now");
                return ctx.Complete($"{Replies.Plain(column.Name)} of {target.Title} has changed since you saw it: it read {Values.Show(changed.Before)} then and reads {Values.Show(changed.After)} now. Nothing was changed; say again if it should be {Values.Show(value)}.");
            }
        }

        var before = current[column.Name];
        using var lease = await ctx.Gate.EnterAsync($"correcting {target.Title}", ctx.Cancellation).ConfigureAwait(false);
        ctx.Cancellation.ThrowIfCancellationRequested();

        var step = ctx.Tracer.Step("the change", input: $"{column.Name}: {Values.Show(before)}");
        try
        {
            ctx.Storage.InUnitOfWork(() =>
            {
                var previous = ctx.Storage.HeadVersion(handle.Thing, target.Id)!.Value;
                ctx.Storage.Update(current.With(column.Name, value));
                ctx.Journal.Updated(ctx.Request.Id, handle.Thing, target.Id, previous, step.Id, "corrected");
            });
        }
        catch (StorageException failure)
        {
            step.Failed(failure.Message);
            return ctx.Fail(failure.Message);
        }

        step.Done($"{column.Name}: {Values.Show(value)}");

        // The handle moves with the person's own correction: the record is now at the version
        // they made, so a second correction of the same record is not "changed since you saw it".
        var made = ctx.Storage.HeadVersion(handle.Thing, target.Id) ?? target.Version;
        var refreshed = handle with { Shown = [.. handle.Shown.Select(shown => shown.Id == target.Id ? shown with { Version = made } : shown)] };

        return ctx.Complete(Replies.Corrected(handle.Thing, target.Title, column.Name, before, value), payload: refreshed.Write());
    }
}
