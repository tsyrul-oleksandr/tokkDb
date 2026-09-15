using System.Diagnostics;
using System.Globalization;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.TokenBudget;

/// <summary>
/// Milestone A, step 5.0: one path end to end against the real transport and a real local model.
/// A sentence of text in, deterministic placement into one thing, an atomic write with its
/// durable change record, one retrieval, and the reply on the console. It reports the tokens,
/// the round trips and the wall clock, for the budget phase to compare against.
/// </summary>
public static class ThinPath
{
    public static async Task<int> RunAsync(string databasePath, TextWriter output)
    {
        using var app = Composition.OverOllama(databasePath);
        var storage = app.Storage;

        output.WriteLine($"database : {databasePath}");
        output.WriteLine($"model    : {app.Runner.ConfigurationFor(Agents.Operations.Operations.Intent).Model}, over Ollama at {Agents.Models.ModelEndpoint.Ollama.Address}");
        output.WriteLine();

        var clock = Stopwatch.StartNew();
        var stored = await app.Orchestrator.HandleAsync(new TurnInput(null, "I want to save this: EuroPython in Prague, from 20 to 24 July 2025, tickets and hotel came to 840 euros."));
        var storing = clock.Elapsed;

        Report(output, "1. storing a sentence", stored, storage.Traces, storing);

        clock.Restart();
        var found = await app.Orchestrator.HandleAsync(new TurnInput(stored.ConversationId, "How much did I spend on conferences?"));
        var finding = clock.Elapsed;

        Report(output, "2. asking for it back", found, storage.Traces, finding);

        if (found.Results is { } page)
        {
            output.WriteLine("   the records behind the answer, rendered by the application and never by the model:");
            for (var i = 0; i < page.Records.Count; i++)
            {
                output.WriteLine($"     {i + 1}. {page.Titles[i]}: " + string.Join(", ", page.Records[i].Fields.Select(field => $"{field.Key}={field.Value}")));
            }
        }

        output.WriteLine();
        output.WriteLine("what is in the storage now:");
        foreach (var definition in storage.GetCollectionDefinitions())
        {
            output.WriteLine($"  {definition.Name}: {storage.GetAll(definition.Name).Count} records, fields {string.Join(", ", definition.Columns.Select(static column => column.Name + ":" + column.Type))}");
        }

        var ok = stored.Succeeded && found.Succeeded && found.Results is { Total: > 0 };
        output.WriteLine();
        output.WriteLine(ok ? "thin path: something typed is in storage and came back out by asking." : "thin path: FAILED - see above.");
        return ok ? 0 : 1;
    }

    private static void Report(TextWriter output, string title, TurnOutcome outcome, ITraceRecorder traces, TimeSpan wall)
    {
        output.WriteLine($"{title}: {outcome.State}");
        output.WriteLine($"   reply: {outcome.Reply}");

        var read = traces.Read(outcome.RequestId);
        if (read is null) return;

        var calls = read.Value.Steps.Where(static step => step.Call is not null).ToList();
        output.WriteLine($"   steps: {string.Join(" → ", read.Value.Steps.Select(static step => step.Name + (step.Status is StepStatus.Completed ? "" : $" ({step.Status})")))}");
        output.WriteLine($"   model calls {calls.Count}, round trips {calls.Sum(static step => step.Call!.RoundTrips)}, retries {calls.Sum(static step => step.Call!.Retries)}, " +
                         $"prompt tokens {calls.Sum(static step => step.Call!.PromptTokens)}, completion tokens {calls.Sum(static step => step.Call!.CompletionTokens)}, " +
                         $"peak context {(calls.Count == 0 ? 0 : calls.Max(static step => step.Call!.PeakContextTokens))}, model time {calls.Sum(static step => step.Call!.Duration.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)} s, " +
                         $"wall clock {wall.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");

        foreach (var step in calls)
        {
            output.WriteLine($"     - {step.Name}: {step.Call!.PromptTokens} in, {step.Call.CompletionTokens} out, {step.Call.RoundTrips} round trips, {step.Call.Duration.TotalSeconds:F1} s");
        }

        var changes = traces.Changes(outcome.RequestId);
        if (changes.Count > 0) output.WriteLine($"   changes recorded: {string.Join(", ", changes.Select(static change => change.Kind))}");
    }
}
