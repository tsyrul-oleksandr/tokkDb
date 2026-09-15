using TokkDb.Assistant.TokenBudget;
using TokkDb.Assistant.TokenBudget.Harness;

var command = args.Length > 0 ? args[0] : "help";

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TokkDb.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? Directory.GetCurrentDirectory();
}
var directory = Path.Combine(Path.GetTempPath(), "tokkdb-assistant-harness");
Directory.CreateDirectory(directory);

switch (command)
{
    case "thin":
    {
        // Step 5.0: a fresh database every run, so the numbers are of the path and not of the history.
        var path = Path.Combine(directory, $"thin-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        return await ThinPath.RunAsync(path, Console.Out);
    }

    case "chat":
    {
        // The minimal chat display of Milestone A: type, and see what came back.
        var path = args.Length > 1 ? args[1] : Path.Combine(directory, "chat.db");
        return await Chat.RunAsync(path, Console.In, Console.Out);
    }

    case "measure":
    {
        // Step 8.1: the scenarios of §5 over the fake model, or over the real one with --ollama,
        // each figure of §6.2a read from the trace and written beside its limit.
        var real = args.Contains("--ollama");
        var runs = args.SkipWhile(static a => a != "--runs").Skip(1).Select(static a => int.TryParse(a, out var n) ? n : 0).FirstOrDefault();
        if (runs <= 0) runs = real ? 3 : TokkDb.Assistant.TokenBudget.Harness.Budgets.MinimumRuns;
        var output = args.SkipWhile(static a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(FindRepositoryRoot(), "docs", "assistant-token-budget.md");
        var only = args.SkipWhile(static a => a != "--only").Skip(1).TakeWhile(static a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        return await Measure.RunAsync(directory, real, runs, output, Console.Out, only);
    }

    case "perf":
    {
        // Step 9.6: the generated fixtures, each timing cold and warm, p50 and p95, the planner path beside it.
        var runs = args.SkipWhile(static a => a != "--runs").Skip(1).Select(static a => int.TryParse(a, out var n) ? n : 0).FirstOrDefault();
        if (runs <= 0) runs = 10;
        var output = args.SkipWhile(static a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(FindRepositoryRoot(), "docs", "assistant-performance.md");
        return await Perf.RunAsync(Path.Combine(directory, "perf"), runs, output, Console.Out);
    }

    default:
        Console.WriteLine("usage: thin | chat [database] | measure [--ollama] [--runs N] [--only S-3 ...] [--out file] | perf [--runs N] [--out file]");
        return command == "help" ? 0 : 2;
}
