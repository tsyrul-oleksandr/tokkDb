using System.Globalization;
using TokkDb.Assistant.Agents.Models;

namespace TokkDb.Assistant.TokenBudget.Harness;

/// <summary>The measure command: every scenario, the given number of times, and the report.</summary>
public static class Measure
{
    public static async Task<int> RunAsync(string directory, bool real, int runs, string outputPath, TextWriter console, IReadOnlyList<string>? only = null, CancellationToken cancellation = default)
    {
        var runner = new ScenarioRunner(Path.Combine(directory, "measure"),
            (path, scripted) => scripted is null ? Composition.OverOllama(path, probe: true) : Composition.OverScript(path, scripted));

        var scenarios = only is { Count: > 0 } ? ScenarioRunner.Scenarios.Where(only.Contains).ToList() : ScenarioRunner.Scenarios;
        console.WriteLine($"measuring {scenarios.Count} scenarios × {runs} runs over {(real ? "Ollama at " + ModelEndpoint.Ollama.Address : "the scripted model")}");
        var measurements = new List<ScenarioMeasurement>();

        foreach (var scenario in scenarios)
        {
            var figures = new List<RunFigures>();
            for (var i = 0; i < runs; i++)
            {
                var run = await runner.RunAsync(scenario, scripted: !real, cancellation: cancellation).ConfigureAwait(false);
                figures.Add(run);
                console.WriteLine($"  {scenario} run {i + 1}: {(run.Succeeded ? "ok" : "FAILED - " + run.Reason)}; prompt {run.PromptTokensMeasured}{(run.ProbedPromptTokens > 0 ? $" (probe; working call said {run.PromptTokens})" : "")}, out {run.OutputTokens}, calls {run.ModelCalls}, trips {run.RoundTrips}, repairs {run.Retries}, peak {run.PeakContext}, {run.Latency.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s" +
                                  (run.Failures.Count > 0 ? "; " + string.Join(", ", run.Failures.Select(Report.Words)) : ""));
                // What the model actually answered, when the run went wrong: the material of 8.2 and 9.7.
                if (!run.Succeeded) foreach (var (operation, answer) in run.Answers) console.WriteLine($"      {operation}: {answer}");
            }

            var measurement = new ScenarioMeasurement(Budgets.For(scenario), figures);
            measurements.Add(measurement);
            console.WriteLine($"  {scenario}: {(measurement.IsWithinBudget ? "within budget" : "NOT within budget: " + string.Join("; ", measurement.Breaches))}");
        }

        var model = real ? ModelConfiguration.Default.Model : "scripted fake";
        var mode = real ? $"the real model over the native transport, {runs} runs per scenario, single-token probe" : $"the scripted fake, {runs} runs per scenario";
        var machine = $"{Environment.MachineName} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores)";
        var markdown = Report.Markdown(measurements, model, mode, machine, DateTimeOffset.Now, probed: real);

        // The real run and the scripted run are two sections of one file, each replaced by its own kind.
        var existing = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellation).ConfigureAwait(false) : "";
        await File.WriteAllTextAsync(outputPath, Sections.Merge(existing, real ? "real" : "scripted", markdown), cancellation).ConfigureAwait(false);
        console.WriteLine($"written {outputPath}");

        return measurements.All(static measurement => measurement.IsWithinBudget) ? 0 : 1;
    }
}

/// <summary>The report file holds two sections, one per kind of model, so that a scripted run does not erase the real figures.</summary>
public static class Sections
{
    public static string Merge(string existing, string kind, string markdown)
    {
        const string begin = "<!-- section:";
        var start = $"{begin}{kind} -->";
        var end = $"<!-- end:{kind} -->";
        var block = $"{start}\n{markdown}\n{end}\n";

        var i = existing.IndexOf(start, StringComparison.Ordinal);
        var j = i < 0 ? -1 : existing.IndexOf(end, i, StringComparison.Ordinal);
        if (i >= 0 && j >= 0) return existing[..i] + block + existing[(j + end.Length + 1)..];

        // The real section reads first, the scripted one after it.
        return kind == "real" ? block + "\n" + existing : existing + (existing.Length > 0 ? "\n" : "") + block;
    }
}
