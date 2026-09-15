using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.TokenBudget.Harness;

/// <summary>How a model call, or a request, went wrong (AG-1e): counted per mode, never summed into one number.</summary>
public enum FailureMode
{
    /// <summary>A malformed answer was sent back and the repaired one was used: not a failed request, but counted, because a loop that fires often is a budget leak.</summary>
    MalformedRepaired = 1,
    MalformedPastBound,
    EmptyReply,
    Timeout,
    Refusal,

    /// <summary>The answer ran past the output cap and was cut off.</summary>
    CutOff,

    /// <summary>The request finished but did not do what the scenario says it does.</summary>
    WrongOutcome,

    /// <summary>The request failed for a reason that is not the model's: a refusal by storage, a budget exceeded before the call.</summary>
    Other
}

/// <summary>One run of one scenario: every figure of §6.2a, read from the trace of the request it made.</summary>
public sealed record RunFigures(
    string Scenario,
    bool Succeeded,
    int PromptTokens,
    int ProbedPromptTokens,
    int OutputTokens,
    int ModelCalls,
    int RoundTrips,
    int Retries,
    int PeakContext,
    TimeSpan Latency,
    IReadOnlyList<FailureMode> Failures,
    IReadOnlyList<(string Operation, bool Answered)> Calls,
    string? Reason)
{
    /// <summary>What each model call answered, bounded, for the console when a run went wrong; never in the report.</summary>
    public IReadOnlyList<(string Operation, string Answer)> Answers { get; init; } = [];

    /// <summary>The prompt figure the budget is compared with: the probe's where one was taken, the working call's otherwise.</summary>
    public int PromptTokensMeasured => ProbedPromptTokens > 0 ? ProbedPromptTokens : PromptTokens;

    public int TotalTokens => PromptTokensMeasured + OutputTokens;

    /// <summary>The figures of one request, from its steps (TR-3): calls, round trips, retries, tokens, peak context, and each call's mode.</summary>
    public static RunFigures From(string scenario, IReadOnlyList<ExecutionStep> steps, bool succeeded, TimeSpan latency, string? reason, IReadOnlyDictionary<string, int>? probed = null, IReadOnlyList<FailureMode>? extra = null)
    {
        var calls = steps.Where(static step => step.Call is not null).ToList();
        var failures = new List<FailureMode>(extra ?? []);
        var made = new List<(string, bool)>();
        var probedTotal = 0;

        foreach (var step in calls)
        {
            var call = step.Call!;
            if (probed is not null && probed.TryGetValue(call.PromptHash, out var count)) probedTotal += count;

            var answered = step.Status is StepStatus.Completed;
            made.Add((step.Name, answered));

            if (answered && call.Retries > 0) failures.Add(FailureMode.MalformedRepaired);
            if (!answered) failures.Add(ModeOf(step.Output, call));
        }

        return new RunFigures(
            scenario, succeeded,
            calls.Sum(static step => step.Call!.PromptTokens),
            probedTotal,
            calls.Sum(static step => step.Call!.CompletionTokens),
            calls.Count,
            calls.Sum(static step => step.Call!.RoundTrips),
            calls.Sum(static step => step.Call!.Retries),
            calls.Count == 0 ? 0 : calls.Max(static step => step.Call!.PeakContextTokens),
            latency, failures, made, reason)
        {
            Answers = [.. calls.Select(step => (step.Name, (step.Output ?? "").ReplaceLineEndings(" ").Trim() is var text && text.Length > 300 ? text[..300] + "…" : text))]
        };
    }

    /// <summary>The mode of a failed call, from what the runner wrote as the step's output and the call's own figures.</summary>
    public static FailureMode ModeOf(string? output, ModelCall call)
    {
        if (output is null) return FailureMode.Other;
        if (output.Contains("produced nothing", StringComparison.OrdinalIgnoreCase)) return FailureMode.EmptyReply;
        if (output.StartsWith("cut off", StringComparison.OrdinalIgnoreCase)) return FailureMode.CutOff;
        if (output.StartsWith("malformed past", StringComparison.OrdinalIgnoreCase)) return FailureMode.MalformedPastBound;
        if (output.Contains("declined", StringComparison.OrdinalIgnoreCase) || output.Contains("refus", StringComparison.OrdinalIgnoreCase)) return FailureMode.Refusal;
        if (output.Contains("cancel", StringComparison.OrdinalIgnoreCase) || output.Contains("timeout", StringComparison.OrdinalIgnoreCase) || output.Contains("not answer in time", StringComparison.OrdinalIgnoreCase) || output.Contains("could not be reached", StringComparison.OrdinalIgnoreCase) || output.Contains("Timeout", StringComparison.Ordinal)) return FailureMode.Timeout;
        return call.Retries > 0 ? FailureMode.MalformedPastBound : FailureMode.Other;
    }
}

/// <summary>The limits a scenario is held to (§6.2, AG-1b, AG-1c): each asserted, none set from taste - the tokens from §6.2's table, the calls and round trips from what the scenario does, the floor and the run count from AG-1c.</summary>
public sealed record ScenarioLimits(
    string Scenario,
    string Title,
    int PromptTokens,
    int OutputTokens,
    int ModelCalls,
    int RoundTrips,
    int Retries,
    int PeakContext,
    TimeSpan Latency,
    double SuccessFloor,
    int MinimumRuns);

/// <summary>§6.2's whole-scenario budgets, and the calls each scenario is known to make.</summary>
public static class Budgets
{
    /// <summary>The window step 0.3 made a per-call option: the peak context is asserted under it.</summary>
    public const int ContextWindow = 16_384;

    /// <summary>AG-1c's floor, provisional until §9.7 measures the real model over enough runs; the harness asserts the lower bound of the interval against it.</summary>
    public const double SuccessFloor = 0.8;

    /// <summary>Fewer runs than this cannot support the floor at 95%: a floor asserted on them fails as thin.</summary>
    public const int MinimumRuns = 20;

    /// <summary>
    /// Round trips are calls plus one: a structured answer may be sent back once for repair
    /// (AG-5), which the real model did in one run of three on 2026-09-15, and a repair is not a
    /// failed request. A second repair on the same call is over the limit, and so is a repair on a
    /// scenario that makes no structured call.
    /// </summary>
    public static readonly IReadOnlyList<ScenarioLimits> Default =
    [
        new("S-1", "store prose, new collection", 9_000, 1_500, 2, 3, 1, ContextWindow, TimeSpan.FromSeconds(60), SuccessFloor, MinimumRuns),
        new("S-2", "store a 1 000-row spreadsheet into an existing collection", 7_000, 800, 1, 2, 1, ContextWindow, TimeSpan.FromSeconds(45), SuccessFloor, MinimumRuns),
        new("S-3", "retrieval", 4_500, 800, 2, 3, 1, ContextWindow, TimeSpan.FromSeconds(45), SuccessFloor, MinimumRuns),
        new("S-4", "follow-up", 2_500, 400, 0, 0, 0, ContextWindow, TimeSpan.FromSeconds(10), SuccessFloor, MinimumRuns),
        new("S-9", "suggestions for the composer", 1_800, 400, 1, 2, 1, ContextWindow, TimeSpan.FromSeconds(20), SuccessFloor, MinimumRuns)
    ];

    public static ScenarioLimits For(string scenario) => Default.First(limits => limits.Scenario == scenario);
}

/// <summary>The Wilson score interval's lower bound: what a sample of this size can claim at 95%.</summary>
public static class Wilson
{
    public static double LowerBound(int successes, int runs, double z = 1.959964)
    {
        if (runs <= 0) return 0;
        var p = (double)successes / runs;
        var z2 = z * z;
        var centre = p + z2 / (2 * runs);
        var spread = z * Math.Sqrt(p * (1 - p) / runs + z2 / (4.0 * runs * runs));
        return Math.Max(0, (centre - spread) / (1 + z2 / runs));
    }
}

/// <summary>A scenario measured over repeated runs: the figures beside their limits, the rate and its lower bound, the product of the per-call rates beside it, and what breached.</summary>
public sealed record ScenarioMeasurement(ScenarioLimits Limits, IReadOnlyList<RunFigures> Runs)
{
    public int RunCount => Runs.Count;
    public int Successes => Runs.Count(static run => run.Succeeded);
    public double SuccessRate => RunCount == 0 ? 0 : (double)Successes / RunCount;
    public double LowerBound => Wilson.LowerBound(Successes, RunCount);

    /// <summary>AG-1d: the per-call success rates after repair, multiplied - a diagnostic beside the measured rate, never a substitute.</summary>
    public double PredictedRate
    {
        get
        {
            var byOperation = Runs.SelectMany(static run => run.Calls).GroupBy(static call => call.Operation).ToList();
            if (byOperation.Count == 0) return SuccessRate;
            return byOperation.Aggregate(1.0, (product, group) => product * group.Count(static call => call.Answered) / group.Count());
        }
    }

    /// <summary>The distance between the measured rate and the predicted one: near zero when failures are independent, wide when they share a cause.</summary>
    public double Distance => SuccessRate - PredictedRate;

    public int MaxPromptTokens => Runs.Count == 0 ? 0 : Runs.Max(static run => run.PromptTokensMeasured);
    public int MaxOutputTokens => Runs.Count == 0 ? 0 : Runs.Max(static run => run.OutputTokens);
    public int MaxTotalTokens => Runs.Count == 0 ? 0 : Runs.Max(static run => run.TotalTokens);
    public int MaxModelCalls => Runs.Count == 0 ? 0 : Runs.Max(static run => run.ModelCalls);
    public int MaxRoundTrips => Runs.Count == 0 ? 0 : Runs.Max(static run => run.RoundTrips);
    public int MaxRetries => Runs.Count == 0 ? 0 : Runs.Max(static run => run.Retries);
    public int MaxPeakContext => Runs.Count == 0 ? 0 : Runs.Max(static run => run.PeakContext);
    public TimeSpan LatencyP50 => Percentile(0.5);
    public TimeSpan LatencyP95 => Percentile(0.95);

    public IReadOnlyDictionary<FailureMode, int> FailuresByMode =>
        Runs.SelectMany(static run => run.Failures).GroupBy(static mode => mode).ToDictionary(static group => group.Key, static group => group.Count());

    /// <summary>Every limit that was not met, in words; empty when the scenario is within budget.</summary>
    public IReadOnlyList<string> Breaches
    {
        get
        {
            var breaches = new List<string>();
            var limits = Limits;
            if (RunCount == 0) breaches.Add("no runs");
            if (MaxPromptTokens > limits.PromptTokens) breaches.Add($"prompt tokens {MaxPromptTokens} over {limits.PromptTokens}");
            if (MaxOutputTokens > limits.OutputTokens) breaches.Add($"output tokens {MaxOutputTokens} over {limits.OutputTokens}");
            if (MaxModelCalls > limits.ModelCalls) breaches.Add($"model calls {MaxModelCalls} over {limits.ModelCalls}");
            if (MaxRoundTrips > limits.RoundTrips) breaches.Add($"round trips {MaxRoundTrips} over {limits.RoundTrips}");
            if (MaxRetries > limits.Retries) breaches.Add($"repairs {MaxRetries} over {limits.Retries}");
            if (MaxPeakContext > limits.PeakContext) breaches.Add($"peak context {MaxPeakContext} over {limits.PeakContext}");
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (RunCount > 0 && LatencyP95 > limits.Latency) breaches.Add($"latency p95 {LatencyP95.TotalSeconds.ToString("0.0", culture)}s over {limits.Latency.TotalSeconds.ToString("0", culture)}s");
            if (RunCount > 0 && RunCount < limits.MinimumRuns) breaches.Add($"thin: {RunCount} runs cannot support a floor of {limits.SuccessFloor.ToString("0.00", culture)} (lower bound {LowerBound.ToString("0.00", culture)}); {limits.MinimumRuns} are needed");
            else if (RunCount > 0 && LowerBound < limits.SuccessFloor) breaches.Add($"success rate {SuccessRate.ToString("0.00", culture)} over {RunCount} runs has a lower bound of {LowerBound.ToString("0.00", culture)}, below the floor {limits.SuccessFloor.ToString("0.00", culture)}");
            return breaches;
        }
    }

    public bool IsWithinBudget => Breaches.Count == 0;

    private TimeSpan Percentile(double p)
    {
        if (Runs.Count == 0) return TimeSpan.Zero;
        var sorted = Runs.Select(static run => run.Latency).OrderBy(static latency => latency).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
