using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.TokenBudget;
using TokkDb.Assistant.TokenBudget.Harness;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The harness (8.1, D-12, NF-1, AG-1a to AG-1e): every scenario has each figure of §6.2a beside its
/// limit, a floor asserted on too few runs fails as thin, a scenario that gains a model call fails
/// until the limit is raised, failures are counted by mode, and S-2's cost does not move with the row count.
/// </summary>
public sealed class TokenBudgetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tokkdb-budget-tests", Guid.NewGuid().ToString("N"));

    private ScenarioRunner Runner => new(_directory, (path, scripted) => Composition.OverScript(path, scripted));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Every_scenario_is_measured_beside_its_limit_and_meets_it_over_the_scripted_model()
    {
        var runner = Runner;
        var runs = Budgets.MinimumRuns;

        foreach (var scenario in ScenarioRunner.Scenarios)
        {
            var figures = new List<RunFigures>();
            for (var i = 0; i < runs; i++) figures.Add(await runner.RunAsync(scenario, scripted: true, rows: 100));

            var measurement = new ScenarioMeasurement(Budgets.For(scenario), figures);

            Assert.True(measurement.IsWithinBudget, $"{scenario}: {string.Join("; ", measurement.Breaches)}; reasons: {string.Join(" | ", figures.Where(static f => !f.Succeeded).Select(static f => f.Reason))}");
            Assert.Equal(runs, measurement.RunCount);
            Assert.Equal(1.0, measurement.SuccessRate);
            Assert.True(measurement.LowerBound >= Budgets.SuccessFloor, $"{scenario}: lower bound {measurement.LowerBound:0.00}");
            Assert.True(measurement.MaxPromptTokens > 0 || measurement.Limits.ModelCalls == 0, $"{scenario} measured no prompt tokens");
            Assert.Equal(measurement.Limits.ModelCalls, measurement.MaxModelCalls);
            Assert.True(measurement.MaxRoundTrips <= measurement.Limits.RoundTrips, $"{scenario}: {measurement.MaxRoundTrips} round trips");
            Assert.Empty(measurement.FailuresByMode);
        }
    }

    [Fact]
    public async Task A_floor_asserted_on_too_few_runs_fails_as_thin()
    {
        var runner = Runner;
        var figures = new List<RunFigures>();
        for (var i = 0; i < 3; i++) figures.Add(await runner.RunAsync("S-3", scripted: true));

        var measurement = new ScenarioMeasurement(Budgets.For("S-3"), figures);

        Assert.Equal(1.0, measurement.SuccessRate);
        Assert.False(measurement.IsWithinBudget);
        Assert.Contains(measurement.Breaches, static breach => breach.StartsWith("thin:", StringComparison.Ordinal));
        Assert.True(measurement.LowerBound < Budgets.SuccessFloor, $"three successes claim a lower bound of {measurement.LowerBound:0.00}");
        Assert.Equal(0.5655, Wilson.LowerBound(5, 5), 3);
        Assert.Equal(0.0, Wilson.LowerBound(0, 0));
    }

    [Fact]
    public async Task A_scenario_that_gains_a_model_call_or_a_round_trip_fails_until_the_limit_is_raised()
    {
        var runner = Runner;
        var figures = new List<RunFigures>();
        for (var i = 0; i < Budgets.MinimumRuns; i++) figures.Add(await runner.RunAsync("S-3", scripted: true));
        var limits = Budgets.For("S-3");

        Assert.True(new ScenarioMeasurement(limits, figures).IsWithinBudget);

        var oneCallFewer = new ScenarioMeasurement(limits with { ModelCalls = limits.ModelCalls - 1 }, figures);
        Assert.Contains(oneCallFewer.Breaches, static breach => breach.StartsWith("model calls", StringComparison.Ordinal));

        var oneTripFewer = new ScenarioMeasurement(limits with { RoundTrips = figures.Max(static f => f.RoundTrips) - 1 }, figures);
        Assert.Contains(oneTripFewer.Breaches, static breach => breach.StartsWith("round trips", StringComparison.Ordinal));

        var fewerTokens = new ScenarioMeasurement(limits with { PromptTokens = figures.Max(static f => f.PromptTokensMeasured) - 1 }, figures);
        Assert.Contains(fewerTokens.Breaches, static breach => breach.StartsWith("prompt tokens", StringComparison.Ordinal));

        var raised = new ScenarioMeasurement(limits with { ModelCalls = limits.ModelCalls + 1 }, figures);
        Assert.True(raised.IsWithinBudget);
    }

    [Fact]
    public async Task Failures_are_counted_by_mode_and_a_repaired_answer_is_not_a_failed_request()
    {
        var runner = Runner;

        var repaired = await runner.RunAsync("S-3", scripted: true, script: static model => model.Malformed("query"));
        Assert.True(repaired.Succeeded, repaired.Reason);
        Assert.Equal([FailureMode.MalformedRepaired], repaired.Failures);
        Assert.Equal(1, repaired.Retries);
        Assert.Equal(3, repaired.RoundTrips);

        var empty = await runner.RunAsync("S-3", scripted: true, script: static model => model.Empty("query"));
        Assert.False(empty.Succeeded);
        Assert.Contains(FailureMode.EmptyReply, empty.Failures);

        var pastBound = await runner.RunAsync("S-3", scripted: true, script: static model => model.Malformed("query", times: 3));
        Assert.False(pastBound.Succeeded);
        Assert.Contains(FailureMode.MalformedPastBound, pastBound.Failures);

        var timedOut = await runner.RunAsync("S-3", scripted: true, script: static model => model.Timeout("query"));
        Assert.False(timedOut.Succeeded);
        Assert.Contains(FailureMode.Timeout, timedOut.Failures);

        var measurement = new ScenarioMeasurement(Budgets.For("S-3"), [repaired, empty, pastBound, timedOut]);
        Assert.Equal(1, measurement.FailuresByMode[FailureMode.MalformedRepaired]);
        Assert.Equal(1, measurement.FailuresByMode[FailureMode.EmptyReply]);
        Assert.Equal(1, measurement.FailuresByMode[FailureMode.MalformedPastBound]);
        Assert.Equal(1, measurement.FailuresByMode[FailureMode.Timeout]);

        // AG-1d: the measured rate is 1/4; the per-call product sees the intent calls all answered and the query calls answered 1 in 4.
        Assert.Equal(0.25, measurement.SuccessRate);
        Assert.Equal(0.25, measurement.PredictedRate, 3);
    }

    [Fact]
    public async Task S2_cost_does_not_move_when_the_row_count_does()
    {
        var runner = Runner;

        var hundred = await runner.RunAsync("S-2", scripted: true, rows: 100);
        var thousand = await runner.RunAsync("S-2", scripted: true, rows: 1_000);

        Assert.True(hundred.Succeeded, hundred.Reason);
        Assert.True(thousand.Succeeded, thousand.Reason);
        Assert.Equal(hundred.PromptTokensMeasured, thousand.PromptTokensMeasured);
        Assert.Equal(hundred.ModelCalls, thousand.ModelCalls);
        Assert.Equal(1, thousand.ModelCalls);
        Assert.True(thousand.PromptTokensMeasured <= Budgets.For("S-2").PromptTokens);
    }

    [Fact]
    public void The_report_puts_each_figure_beside_its_limit_and_keeps_both_sections()
    {
        var limits = Budgets.For("S-4");
        var figures = Enumerable.Range(0, Budgets.MinimumRuns).Select(_ => new RunFigures("S-4", true, 0, 0, 0, 0, 0, 0, 0, TimeSpan.FromMilliseconds(3), [], [], null)).ToList();
        var markdown = Report.Markdown([new ScenarioMeasurement(limits, figures)], "scripted fake", "test", "here", DateTimeOffset.Now, probed: false);

        Assert.Contains("| model calls (max) | 0 | 0 | met |", markdown);
        Assert.Contains("| prompt tokens (max over runs) | 0 | 2500 | met |", markdown);
        Assert.Contains("success rate | 20/20 = 1.00, lower bound 0.84 at 95%", markdown);
        Assert.Contains("Within budget.", markdown);

        var merged = Sections.Merge("", "scripted", markdown);
        var both = Sections.Merge(merged, "real", "REAL");
        Assert.StartsWith("<!-- section:real -->", both);
        Assert.Contains("<!-- section:scripted -->", both);
        var replaced = Sections.Merge(both, "scripted", "AGAIN");
        Assert.Contains("AGAIN", replaced);
        Assert.DoesNotContain("| model calls (max) |", replaced);
        Assert.Contains("REAL", replaced);
    }
}
