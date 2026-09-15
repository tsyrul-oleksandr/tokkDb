using System.Diagnostics;
using System.Text;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.TokenBudget.Harness;

/// <summary>
/// The scenarios of §5 that carry a budget, each as one measured request over a fresh database
/// with its precondition put in place by storage rather than by a turn - so the figures are of
/// the scenario and not of its history. Over the scripted model the answers are the scenario's
/// own scripts; over the real model nothing is scripted and the same criteria decide success.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly Func<string, ScriptedModel?, Composition> _compose;
    private readonly string _directory;

    public ScenarioRunner(string directory, Func<string, ScriptedModel?, Composition> compose)
    {
        _directory = directory;
        _compose = compose;
        Directory.CreateDirectory(directory);
    }

    public static readonly IReadOnlyList<string> Scenarios = ["S-1", "S-2", "S-3", "S-4", "S-9"];

    /// <summary>What a scenario says, for the report.</summary>
    public static string Said(string scenario) => scenario switch
    {
        "S-1" => "I want to save these — the conference in Lviv on 14 March 2025, 12 000 hryvnia, and the one in Kyiv on 20 May 2025, 8 500.",
        "S-2" => "Here are more of them (with conferences-2025.csv attached)",
        "S-3" => "How much did I spend on conferences last year?",
        "S-4" => "Which was the most expensive?",
        "S-9" => "(Suggest, after the retrieval, with nothing typed)",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    /// <param name="script">What the fake answers before the scenario's own answers: a malformed or empty reply to inject, for the modes of AG-1e.</param>
    public async Task<RunFigures> RunAsync(string scenario, bool scripted, int rows = 1_000, Action<ScriptedModel>? script = null, CancellationToken cancellation = default)
    {
        var path = Path.Combine(_directory, $"{scenario}-{Guid.NewGuid():N}.db");
        var model = scripted ? new ScriptedModel() : null;
        using var app = _compose(path, model);
        var probe = app.Probe;

        try
        {
            return scenario switch
            {
                "S-1" => await S1Async(app, model, script, cancellation).ConfigureAwait(false),
                "S-2" => await S2Async(app, model, rows, script, cancellation).ConfigureAwait(false),
                "S-3" => await S3Async(app, model, script, cancellation).ConfigureAwait(false),
                "S-4" => await S4Async(app, model, script, cancellation).ConfigureAwait(false),
                "S-9" => await S9Async(app, model, script, cancellation).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
        }
        finally
        {
            app.Dispose();
            foreach (var file in Directory.GetFiles(_directory, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }

    private static async Task<RunFigures> S1Async(Composition app, ScriptedModel? model, Action<ScriptedModel>? script, CancellationToken cancellation)
    {
        if (model is not null) script?.Invoke(model);
        model?.Answer("intent", Agents.Testing.Scenarios.Intent("store")).Answer("extraction", Agents.Testing.Scenarios.ExtractedConferences);

        var (outcome, latency) = await TimedAsync(app, new TurnInput(null, Said("S-1")), cancellation).ConfigureAwait(false);
        var things = app.Storage.GetCollectionDefinitions();
        var kept = things.Count == 1 && app.Storage.GetAll(things.First().Name).Count == 2;
        return Figures("S-1", app, outcome, latency, outcome.Succeeded && kept, kept ? null : "expected one new thing holding two records");
    }

    private static async Task<RunFigures> S2Async(Composition app, ScriptedModel? model, int rows, Action<ScriptedModel>? script, CancellationToken cancellation)
    {
        Agents.Testing.Scenarios.GivenConferences(app.Storage);
        if (model is not null) script?.Invoke(model);
        model?.Answer("mapping", Agents.Testing.Scenarios.MapOntoConferences);

        var file = new SeparatedValuesParser().Read(new MemoryStream(Encoding.UTF8.GetBytes(Spreadsheet(rows))), "conferences-2025");
        var (outcome, latency) = await TimedAsync(app, new TurnInput(null, "Here are more of them", Files: [file]), cancellation).ConfigureAwait(false);
        var kept = app.Storage.GetAll("conferences").Count == rows;
        return Figures("S-2", app, outcome, latency, outcome.Succeeded && kept, kept ? null : $"expected {rows} records in conferences, found {app.Storage.GetAll("conferences").Count}");
    }

    private static async Task<RunFigures> S3Async(Composition app, ScriptedModel? model, Action<ScriptedModel>? script, CancellationToken cancellation)
    {
        GivenFourConferences(app.Storage);
        if (model is not null) script?.Invoke(model);
        model?.Answer("intent", Agents.Testing.Scenarios.Intent("find")).Answer("query", Agents.Testing.Scenarios.ConferencesLastYear);

        var (outcome, latency) = await TimedAsync(app, new TurnInput(null, Said("S-3")), cancellation).ConfigureAwait(false);
        var shown = outcome.Results is { Total: 4 };
        return Figures("S-3", app, outcome, latency, outcome.Succeeded && shown, shown ? null : "expected the four conferences to be found");
    }

    private static async Task<RunFigures> S4Async(Composition app, ScriptedModel? model, Action<ScriptedModel>? script, CancellationToken cancellation)
    {
        GivenFourConferences(app.Storage);
        model?.Answer("intent", Agents.Testing.Scenarios.Intent("find")).Answer("query", Agents.Testing.Scenarios.ConferencesLastYear);

        // The retrieval it follows is the precondition, not the measurement.
        var before = await app.Orchestrator.HandleAsync(new TurnInput(null, Said("S-3")), cancellation).ConfigureAwait(false);
        if (!before.Succeeded || before.Results is not { Total: 4 })
        {
            // What the retrieval answered goes to the console, since the precondition is not measured but its failure is the run's.
            var steps = app.Storage.Traces.Read(before.RequestId)?.Steps ?? [];
            return new RunFigures("S-4", false, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, [FailureMode.Other], [], "the retrieval before it did not succeed: " + before.Reply)
            {
                Answers = [.. steps.Where(static step => step.Call is not null).Select(step => (step.Name, (step.Output ?? "").ReplaceLineEndings(" ")))]
            };
        }

        if (model is not null) script?.Invoke(model);
        model?.Answer("intent", Agents.Testing.Scenarios.Intent("follow-up"));

        var (outcome, latency) = await TimedAsync(app, new TurnInput(before.ConversationId, Said("S-4")), cancellation).ConfigureAwait(false);
        var answered = outcome.Reply.Contains("PyCon Lviv", StringComparison.Ordinal);
        return Figures("S-4", app, outcome, latency, outcome.Succeeded && answered, answered ? null : "expected PyCon Lviv to be named as the most expensive");
    }

    /// <summary>S-9: suggestions after a retrieval, which is the precondition and not the measurement.</summary>
    private static async Task<RunFigures> S9Async(Composition app, ScriptedModel? model, Action<ScriptedModel>? script, CancellationToken cancellation)
    {
        GivenFourConferences(app.Storage);
        model?.Answer("intent", Agents.Testing.Scenarios.Intent("find")).Answer("query", Agents.Testing.Scenarios.ConferencesLastYear);
        // After S-3, whatever it found: the suggestions call is what is measured here, and a
        // query the real model got wrong is S-3's failure, counted there and not again as S-9's.
        var before = await app.Orchestrator.HandleAsync(new TurnInput(null, Said("S-3")), cancellation).ConfigureAwait(false);
        if (!before.Succeeded)
        {
            return new RunFigures("S-9", false, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, [FailureMode.Other], [], "the retrieval before it did not complete: " + before.Reply);
        }

        if (model is not null) script?.Invoke(model);
        model?.Answer("suggestions", """{"suggestions":["Which of those was the most expensive?","Show my conferences from this year","The Lviv one was actually 13 000","Remove the Kyiv one"]}""");

        var clock = Stopwatch.StartNew();
        var set = await app.Orchestrator.SuggestAsync(before.ConversationId, null, cancellation).ConfigureAwait(false);
        var latency = clock.Elapsed;

        var steps = app.Storage.Traces.Read(set.RequestId)?.Steps ?? [];
        var ok = set.FromModel && set.Options.Count >= SuggestionSet.Fewest && set.Options.Count <= SuggestionSet.Most;
        return RunFigures.From("S-9", steps, ok, latency, ok ? null : $"expected 3 to 7 options from the model, got {set.Options.Count}{(set.FromModel ? "" : " from the fallback")}", app.Probe?.Probed, ok ? null : [FailureMode.WrongOutcome])
            with { Answers = [.. steps.Where(static step => step.Call is not null).Select(step => (step.Name, (step.Output ?? "").ReplaceLineEndings(" ")))] };
    }

    private static async Task<(TurnOutcome Outcome, TimeSpan Latency)> TimedAsync(Composition app, TurnInput input, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var outcome = await app.Orchestrator.HandleAsync(input, cancellation).ConfigureAwait(false);
        return (outcome, clock.Elapsed);
    }

    private static RunFigures Figures(string scenario, Composition app, TurnOutcome outcome, TimeSpan latency, bool succeeded, string? reason)
    {
        var steps = app.Storage.Traces.Read(outcome.RequestId)?.Steps ?? [];
        var extra = new List<FailureMode>();
        if (!succeeded && outcome.Succeeded) extra.Add(FailureMode.WrongOutcome);
        if (outcome.Failure is not null && !steps.Any(static step => step.Call is not null && step.Status is not Trace.StepStatus.Completed)) extra.Add(FailureMode.Other);
        return RunFigures.From(scenario, steps, succeeded, latency, reason ?? outcome.Failure, app.Probe?.Probed, extra);
    }

    private static void GivenFourConferences(IStorage storage)
    {
        Agents.Testing.Scenarios.GivenConferences(storage);
        storage.InUnitOfWork(() =>
        {
            foreach (var line in Agents.Testing.Scenarios.ConferencesCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var cells = line.Split(',');
                storage.Create("conferences", new Dictionary<string, object?>
                {
                    ["name"] = cells[0], ["city"] = cells[1], ["date"] = DateOnly.Parse(cells[2], System.Globalization.CultureInfo.InvariantCulture), ["cost"] = decimal.Parse(cells[3], System.Globalization.CultureInfo.InvariantCulture)
                });
            }
        });
    }

    /// <summary>A spreadsheet of the given size, the same shape as S-1's thing plus the notes column: what S-2 drops.</summary>
    public static string Spreadsheet(int rows)
    {
        var text = new StringBuilder("name,city,date,cost,notes\n");
        var cities = new[] { "Lviv", "Kyiv", "Prague", "Vilnius", "Vienna", "Kraków" };
        for (var i = 0; i < rows; i++)
        {
            var day = new DateOnly(2025, 1, 1).AddDays(i % 360);
            text.Append("Conference ").Append(i + 1).Append(',').Append(cities[i % cities.Length]).Append(',')
                .Append(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(400 + (i * 37) % 9_000).Append(',').Append(i % 3 == 0 ? "tickets and hotel" : "").Append('\n');
        }

        return text.ToString();
    }
}
