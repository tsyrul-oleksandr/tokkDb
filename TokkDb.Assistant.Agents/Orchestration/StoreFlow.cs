using System.Globalization;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Storing what a person brought (S-1, S-2, N-1 to N-5, N-9, N-10, N-12): the incoming shape, the
/// shortlist, the mapping call where there is something to choose between, the proposal with its
/// C#-computed confidence, the decision to apply or to ask, and the write in one unit of work
/// with its change records.
/// </summary>
internal static class StoreFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, IReadOnlyList<ParsedFile> files)
    {
        var shapes = new List<IncomingShape>();

        foreach (var file in files.Where(static file => file.IsTabular))
        {
            var read = IncomingShape.FromTables(file.Tables, file.Name);
            ctx.Tracer.Note("what is in the file",
                input: file.Name + file.Extension,
                output: string.Join("; ", read.Select(shape => $"{shape.Source}: {shape.Rows.Count} rows, {shape.Fields.Count} fields ({string.Join(", ", shape.FieldNames)})")));
            shapes.AddRange(read);
        }

        foreach (var file in files.Where(static file => file.IsProse))
        {
            shapes.AddRange(await ExtractAsync(ctx, file.Prose!, file.Name + file.Extension).ConfigureAwait(false));
        }

        if (files.Count == 0 && ctx.Text.Length > 0)
        {
            shapes.AddRange(await ExtractAsync(ctx, ProseFile.ReadText(System.Text.Encoding.UTF8.GetBytes(ctx.Text), "message"), "the message").ConfigureAwait(false));
        }

        var usable = shapes.Where(static shape => !shape.IsEmpty).ToList();
        if (usable.Count == 0)
        {
            // N-5: headings and no rows, or a message with nothing in it. Nothing is created.
            ctx.Tracer.Note("what is in it", output: "nothing to keep");
            return ctx.Complete(Replies.NothingToStore(files.Count > 0));
        }

        var replies = new List<string>();
        string? committed = null;

        foreach (var shape in usable)
        {
            var (validated, decision) = await ProposeAsync(ctx, shape).ConfigureAwait(false);

            if (decision is PlacementDecision.Ask)
            {
                var proposal = validated.Proposal;
                return ctx.Ask(
                    PlacementCard(ctx, proposal),
                    IntentPayloads.Placement(shape, LastAnswer, ctx.Options.MergePolicy, proposal.SchemaVersion, ctx.Text, validated.Hash));
            }

            var (reply, hash) = await ExecuteAsync(ctx, validated).ConfigureAwait(false);
            replies.Add(reply);
            committed = hash;
        }

        return ctx.Complete(string.Join(" ", replies), committedHash: committed);
    }

    /// <summary>The answer was yes: re-plan from the held recipe and run exactly what was shown, or say why not (AG-8, AG-9, AG-3d).</summary>
    public static async Task<TurnOutcome> ResumeAsync(TurnContext ctx)
    {
        var intent = ctx.Request.Intent ?? throw new InvalidOperationException("The request holds nothing to resume.");
        var (shape, answer, policy, schemaThen, message) = IntentPayloads.ReadPlacement(intent.Payload);

        if (ctx.Request.HasCommitted(intent.Hash))
        {
            return ctx.Complete(Replies.AlreadyDone());
        }

        var shortlist = CollectionPrefilter.Shortlist(ctx.Storage.GetCollectionDefinitions(), shape.FieldNames, message);
        var proposal = ctx.Placer.Propose(shape, shortlist, answer);
        var rows = Placer.Rows(shape, proposal);

        ValidatedProposal validated;
        try
        {
            validated = ValidatedProposal.Validate(ctx.Storage, proposal, rows, policy);
        }
        catch (InvalidProposalException invalid)
        {
            return ctx.Fail("what was agreed no longer fits what is stored: " + string.Join(" ", invalid.Problems));
        }

        if (!string.Equals(validated.Hash, intent.Hash, StringComparison.Ordinal))
        {
            var schemaNow = SchemaVersion.Of(ctx.Storage);
            if (string.Equals(schemaNow, schemaThen, StringComparison.Ordinal))
            {
                // N-12: the storage is as it was, so the proposal is not the one that was shown.
                ctx.Tracer.Note("the check", output: "the proposal differs from the one that was confirmed; refused");
                return ctx.Fail("what was about to be applied is not what you were shown, so it was not applied");
            }

            // AG-9, N-10: the shape of the storage moved between the question and the answer: re-plan.
            ctx.Tracer.Note("re-planned", input: "the storage changed since the question was asked", output: proposal.Describe());

            if (PlacementDecisions.Decide(proposal, ctx.Options.Thresholds) is PlacementDecision.Ask)
            {
                return ctx.Ask(PlacementCard(ctx, proposal, "Things changed since you were asked, so here it is again."),
                    IntentPayloads.Placement(shape, answer, policy, proposal.SchemaVersion, message, validated.Hash));
            }
        }

        var (reply, hash) = await ExecuteAsync(ctx, validated).ConfigureAwait(false);
        return ctx.Complete(reply, committedHash: hash);
    }

    // ---- The pieces ----------------------------------------------------------------------------------------

    [ThreadStatic]
    private static MappingAnswer? LastAnswer;

    private static async Task<IReadOnlyList<IncomingShape>> ExtractAsync(TurnContext ctx, ProseDocument prose, string source)
    {
        var chunks = ProseChunking.Chunk(prose);
        var shapes = new List<IncomingShape>();

        if (chunks.Count == 0 || chunks.All(static chunk => string.IsNullOrWhiteSpace(chunk.Text)))
        {
            return shapes;
        }

        // One extraction per chunk (IN-3): each sees one bounded piece of text and nothing else.
        var records = new List<IReadOnlyList<(string, string, string)>>();
        string kind = "things";

        foreach (var chunk in chunks)
        {
            var result = await ctx.RunAsync(Catalogue.Extraction, "text:\n" + chunk.Marked, EgressClass.RawText, Answers.Extraction,
                summary: $"{source}: {Tokens.Estimate(chunk.Text)} tokens of text", withDigest: false).ConfigureAwait(false);

            if (result.Value.Records.Count > 0)
            {
                kind = result.Value.Kind;
                records.AddRange(result.Value.Records);
            }
        }

        if (records.Count > 0) shapes.Add(IncomingShape.FromExtraction(kind, records, source));
        return shapes;
    }

    private static async Task<(ValidatedProposal Validated, PlacementDecision Decision)> ProposeAsync(TurnContext ctx, IncomingShape shape)
    {
        var definitions = ctx.Storage.GetCollectionDefinitions();
        var shortlist = CollectionPrefilter.Shortlist(definitions, shape.FieldNames, ctx.Text);

        MappingAnswer? answer = null;
        var named = Intents.NamedThing(ctx.Text);
        if (named is not null)
        {
            // The person said where it goes: an existing thing by that name, or a new one so called.
            var wanted = Placement.IncomingShape.AsThingName(named);
            var existing = definitions.FirstOrDefault(definition => Names.Same(Placement.IncomingShape.AsThingName(definition.Name), wanted));
            answer = existing is not null ? new MappingAnswer(existing.Name, null, null, []) : new MappingAnswer("new", wanted, null, []);
            if (existing is not null && !shortlist.Any(candidate => Names.Same(candidate.Definition.Name, existing.Name)))
            {
                shortlist = [.. shortlist, new PlacementCandidate(existing, 1, [], [])];
            }

            ctx.Tracer.Note("where it belongs", input: $"you said: {named}", output: existing is not null ? $"{Replies.Plain(existing.Name)}, as you said" : $"a new thing called {Replies.Plain(wanted)}, as you said");
        }
        else if (shortlist.Count > 0)
        {
            var content = shape.Describe() + "\n" + CollectionPrefilter.Describe(shortlist)
                          + (ctx.Text.Length > 0 ? "\nmessage: " + Shorten(ctx.Text, 300) : "");
            var result = await ctx.RunAsync(Catalogue.Mapping, content, EgressClass.BoundedSample, Answers.Mapping(shortlist),
                summary: $"{shape.Rows.Count} rows of {shape.Name}; offered {string.Join(", ", shortlist.Select(static candidate => candidate.Definition.Name))}").ConfigureAwait(false);
            answer = result.Value;
        }

        LastAnswer = answer;

        var proposal = ctx.Placer.Propose(shape, shortlist, answer);

        // A name the person gave is not a guess to be weighed against the shortlist: the
        // confidence is theirs, and only a change that would lose something still asks.
        if (named is not null)
        {
            proposal = proposal with { Confidence = new Confidence(1.0, [.. proposal.Confidence.Evidence, $"you said where it goes: {named}"], null, null) };
        }

        var rows = Placer.Rows(shape, proposal);
        var validated = ValidatedProposal.Validate(ctx.Storage, proposal, rows, ctx.Options.MergePolicy);
        var decision = PlacementDecisions.Decide(proposal, ctx.Options.Thresholds);

        ctx.Tracer.Note("the placement",
            input: CollectionPrefilter.Describe(shortlist),
            output: proposal.Describe() + "; " + string.Join("; ", proposal.Confidence.Evidence)
                    + (decision is PlacementDecision.Ask ? "; asked: " + PlacementDecisions.WhyAsked(proposal, ctx.Options.Thresholds) : "; applied without asking"));

        return (validated, decision);
    }

    /// <summary>The write, in one unit of work with its change records (SC-5, TR-4, IN-8a).</summary>
    private static async Task<(string Reply, string Hash)> ExecuteAsync(TurnContext ctx, ValidatedProposal validated)
    {
        var proposal = validated.Proposal;

        using var lease = await ctx.Gate.EnterAsync($"storing {proposal.Target}", ctx.Cancellation).ConfigureAwait(false);
        ctx.Cancellation.ThrowIfCancellationRequested();

        // A new thing's fields are its shape, not fields added to something: only an existing
        // target gains fields worth saying so about.
        var newFields = proposal.IsNew ? [] : proposal.NewFields.Select(static proposed => proposed.Column.Name).ToList();
        ImportReport report = null!;

        foreach (var action in proposal.Actions.Where(static action => action.Action is not StoreRecords))
        {
            var structural = ctx.Tracer.Step(action.Action is CreateThing ? "the new thing" : "the new field", input: action.Description);
            try
            {
                ctx.Storage.InUnitOfWork(() => StructuralExecutor.Apply(ctx, action, structural.Id));
                structural.Done(action.Evidence.Sentence);
            }
            catch (StorageException failure)
            {
                structural.Failed(failure.Message);
                throw;
            }
        }

        var write = ctx.Tracer.Step("the write", input: $"{validated.RowCount} rows into {proposal.Target}");

        try
        {
            ctx.Storage.InUnitOfWork(() =>
            {
                var importer = new RecordImporter(ctx.Storage);

                var heads = validated.Policy is MergePolicy.UpdateExisting
                    ? ctx.Storage.GetAll(proposal.Target).ToDictionary(static record => record.Id, record => ctx.Storage.HeadVersion(proposal.Target, record.Id)!.Value)
                    : null;

                report = importer.Import(new ImportRequest(proposal.Target, validated.Rows, validated.Policy, proposal.KeyColumn));

                foreach (var row in report.Rows)
                {
                    switch (row.Outcome)
                    {
                        case RowOutcome.Inserted:
                            ctx.Journal.Inserted(ctx.Request.Id, proposal.Target, row.RecordId!.Value, write.Id, row.ToString());
                            break;
                        case RowOutcome.Updated when heads is not null && row.RecordId is { } updated:
                            ctx.Journal.Updated(ctx.Request.Id, proposal.Target, updated, heads[updated], write.Id, row.ToString());
                            break;
                    }
                }
            });
        }
        catch (StorageException failure)
        {
            write.Failed(failure.Message);
            throw;
        }

        write.Done(report.Describe());
        return (Replies.Stored(proposal, report, newFields), validated.Hash);
    }

    /// <summary>The card for a placement that has to be asked about (N-1): the candidates, with what each matched on.</summary>
    private static ConfirmationCard PlacementCard(TurnContext ctx, PlacementProposal proposal, string? preface = null)
    {
        var lines = new List<string>();
        if (preface is not null) lines.Add(preface);

        if (proposal.IsNew)
        {
            lines.Add($"Nothing you have stored looks quite like this, so it would become a new thing called {Replies.Plain(proposal.Target)} with {proposal.NewFields.Count} fields: {string.Join(", ", proposal.NewFields.Select(static proposed => Replies.Plain(proposed.Column.Name)))}.");
            if (proposal.Confidence.RunnerUpName is { } closest)
            {
                lines.Add($"The closest thing you have is {Replies.Plain(closest)}.");
            }
        }
        else
        {
            lines.Add($"It looks most like {Replies.Plain(proposal.Target)}: {string.Join(", ", proposal.Mappings.Select(static mapping => Replies.Plain(mapping.Incoming) + " as " + Replies.Plain(mapping.Existing)))}.");
            if (proposal.Confidence.RunnerUpName is { } other && proposal.Confidence.Margin < ctx.Options.Thresholds.Gap)
            {
                var runnerUp = proposal.Shortlist.FirstOrDefault(candidate => Names.Same(candidate.Definition.Name, other));
                lines.Add($"It could also be {Replies.Plain(other)}" +
                          (runnerUp is { MatchedFields.Count: > 0 } ? $", which has {string.Join(", ", runnerUp.MatchedFields.Select(Replies.Plain))} in common." : "."));
            }

            if (proposal.NewFields.Count > 0)
            {
                lines.Add($"{proposal.NewFields.Count} fields would be new: {string.Join(", ", proposal.NewFields.Select(static proposed => Replies.Plain(proposed.Column.Name)))}.");
            }
        }

        foreach (var action in proposal.Actions.Where(static action => action.NeedsConfirmation))
        {
            lines.Add(char.ToUpperInvariant(action.Description[0]) + action.Description[1..] + ": " + action.Evidence.Sentence + ".");
        }

        lines.Add($"{proposal.Actions.OfType<ClassifiedChange>().Select(static action => action.Action).OfType<StoreRecords>().Sum(static store => store.Count).ToString(CultureInfo.InvariantCulture)} rows would be kept.");

        return new ConfirmationCard(
            proposal.IsNew ? $"Start keeping {Replies.Plain(proposal.Target)}?" : $"Add these to {Replies.Plain(proposal.Target)}?",
            lines,
            [.. proposal.Actions.SelectMany(static action => action.Evidence.Examples).Distinct().Take(ChangeClassifier.ExamplesShown)],
            CanBeUndone: proposal.Actions.All(static action => action.Reversibility is not Trace.Reversibility.NotReversible),
            proposal.Actions.Any(static action => action.Reversibility is Trace.Reversibility.NotReversible) ? ConfirmationCard.CannotBeUndone : "This can be undone.",
            Yes: proposal.IsNew ? "Keep them there" : $"Add them to {Replies.Plain(proposal.Target)}",
            No: "Don't");
    }

    private static string Shorten(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";
}
