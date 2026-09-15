using System.Diagnostics;
using System.Globalization;
using System.Text;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.TokenBudget.Harness;

/// <summary>One timing of the report (NF-6): the figure, what it was measured against, how, and the path the planner took.</summary>
public sealed record Timing(
    string Requirement,
    string What,
    string Fixture,
    string Mode,
    int Runs,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan Limit,
    string? PathRequired,
    string? PathTaken,
    string Note)
{
    public bool TimeMet => P95 <= Limit;
    /// <summary>Any of the paths named, separated by "|", satisfies the requirement; a scan never does.</summary>
    public bool PathMet => PathRequired is null || PathTaken is not null && PathRequired.Split('|').Any(required => PathTaken.Contains(required, StringComparison.OrdinalIgnoreCase));
    public bool Met => TimeMet && PathMet;
}

/// <summary>
/// The performance report (9.6, NF-2, NF-3, NF-5, NF-6, BR-1a): generated fixtures with their
/// record and field counts, each timing cold and warm over a stated number of runs, p50 and p95,
/// the planner path beside it - a number met by a full scan where a seek was required fails - and
/// the machine named in the report rather than in the requirement.
/// </summary>
public static class Perf
{
    public const int LargeRecords = 100_000;
    public const int Traces = 1_000;
    public const int IngestionRows = 10_000;

    public static async Task<int> RunAsync(string directory, int runs, string outputPath, TextWriter console, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(directory);
        var large = Path.Combine(directory, $"perf-large-{LargeRecords}-{Traces}.db");
        var small = Path.Combine(directory, "perf-small-1000.db");

        console.WriteLine($"fixture: {LargeRecords} records of 8 fields and {Traces} traces at {large}");
        if (!File.Exists(large)) Build(large, LargeRecords, Traces, console);
        if (!File.Exists(small)) Build(small, 1_000, 10, console);

        var timings = new List<Timing>();
        var fixture = $"{LargeRecords} records × 8 fields (text, whole number, amount, day, moment, yes/no, text, text), {Traces} requests × 3 steps";

        // NF-3: opening, cold - the first open in this process - then warm.
        var cold = Measure(1, () => OpenAndList(large));
        var warm = Measure(runs, () => OpenAndList(large));
        timings.Add(new Timing("NF-3", "open the database and list the conversations and the overview", fixture, "cold (first open after process start)", 1, cold.P50, cold.P95, TimeSpan.FromSeconds(2), null, null, "the OS file cache is not cleared: cold is the process's first open"));
        timings.Add(new Timing("NF-3", "open the database and list the conversations and the overview", fixture, "warm", runs, warm.P50, warm.P95, TimeSpan.FromSeconds(2), null, null, ""));

        // NF-2: a retrieval of 100 records, ordered, by an index - the planner's path is required.
        using (var storage = new TokkDbStorage(large))
        {
            string? path = null;
            var retrieval = Measure(runs, () =>
            {
                var result = storage.ExecuteQuery(new StorageQuery("entries", [new QueryCondition("category", QueryOperator.Equals, "travel")], [new QuerySort("day", Descending: false)], take: 100));
                path = $"{result.Execution.Access}: {result.Execution.Description}";
                if (result.Records.Count != 100) throw new InvalidOperationException($"expected 100 records, got {result.Records.Count}");
            });
            timings.Add(new Timing("NF-2", "retrieve 100 records of one category, oldest first", fixture, "warm", runs, retrieval.P50, retrieval.P95, TimeSpan.FromSeconds(2), "IndexSeek|RangeWalk", path,
                "excluding model time: the query is run as the assistant runs it, after the model wrote it; the first run of a sort over a thing this large raises the index it then walks, which is where a p95 above the p50 comes from"));

            // BR-1a: the overview costs the catalogue, whatever the record count.
            var before = storage.PageReadCount;
            _ = storage.Overview();
            var largeReads = storage.PageReadCount - before;
            using var smallStorage = new TokkDbStorage(small);
            var beforeSmall = smallStorage.PageReadCount;
            _ = smallStorage.Overview();
            var smallReads = smallStorage.PageReadCount - beforeSmall;
            var overview = Measure(runs, () => _ = storage.Overview());
            timings.Add(new Timing("BR-1a", "the overview", fixture, "warm", runs, overview.P50, overview.P95, TimeSpan.FromMilliseconds(200), null, null,
                $"pages read: {largeReads} at {LargeRecords} records, {smallReads} at 1 000 records" + (largeReads <= smallReads + 2 ? " - does not grow with the records" : " - GROWS WITH THE RECORDS")));
            if (largeReads > smallReads + 2) timings[^1] = timings[^1] with { PathRequired = "constant", PathTaken = "grows" };
        }

        // NF-5: an ingestion of 10 000 rows in one transaction, through the importer the assistant uses.
        var ingestion = Measure(Math.Max(1, Math.Min(runs, 3)), () =>
        {
            var path = Path.Combine(directory, $"perf-ingest-{Guid.NewGuid():N}.db");
            try
            {
                using var storage = new TokkDbStorage(path);
                storage.CreateCollection(new CollectionDefinition("conferences", "conferences", columns:
                [
                    new ColumnDefinition("name", ColumnType.Text, required: true),
                    new ColumnDefinition("city", ColumnType.Text),
                    new ColumnDefinition("date", ColumnType.Date),
                    new ColumnDefinition("cost", ColumnType.Decimal),
                    new ColumnDefinition("notes", ColumnType.Text)
                ]));
                var rows = Enumerable.Range(0, IngestionRows).Select(i => new ImportRow(new Dictionary<string, object?>
                {
                    ["name"] = $"Conference {i}", ["city"] = i % 2 == 0 ? "Lviv" : "Kyiv", ["date"] = new DateOnly(2025, 1, 1).AddDays(i % 360), ["cost"] = (decimal)(400 + i % 9000), ["notes"] = i % 3 == 0 ? "tickets and hotel" : null
                }, i + 2)).ToList();
                ImportReport? report = null;
                storage.InUnitOfWork(() => report = new RecordImporter(storage).Import(new ImportRequest("conferences", rows)));
                if (report!.Inserted != IngestionRows) throw new InvalidOperationException($"expected {IngestionRows} inserted, got {report.Inserted}");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path) + "*")) File.Delete(file);
            }
        });
        timings.Add(new Timing("NF-5", $"ingest {IngestionRows} rows in one transaction", $"{IngestionRows} rows × 5 fields, no key, fingerprint identity", "warm", Math.Max(1, Math.Min(runs, 3)), ingestion.P50, ingestion.P95, TimeSpan.FromSeconds(30), null, null, "excluding model time: the importer is run as the store flow runs it"));

        var machine = $"{Environment.MachineName} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores)";
        var markdown = Markdown(timings, machine, DateTimeOffset.Now);
        await File.WriteAllTextAsync(outputPath, markdown, cancellation).ConfigureAwait(false);
        console.WriteLine(markdown);
        console.WriteLine($"written {outputPath}");
        return timings.All(static timing => timing.Met) ? 0 : 1;
    }

    private static void OpenAndList(string path)
    {
        using var storage = new TokkDbStorage(path);
        _ = storage.Conversations.All();
        _ = storage.Overview();
    }

    /// <summary>The fixture: a thing of the given size with an index on the category, and requests with three steps each.</summary>
    private static void Build(string path, int records, int traces, TextWriter console)
    {
        var clock = Stopwatch.StartNew();
        using var storage = new TokkDbStorage(path);
        storage.CreateCollection(new CollectionDefinition("entries", "entries", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, required: true),
            new ColumnDefinition("number", ColumnType.Integer),
            new ColumnDefinition("amount", ColumnType.Decimal),
            new ColumnDefinition("day", ColumnType.Date),
            new ColumnDefinition("moment", ColumnType.Timestamp),
            new ColumnDefinition("done", ColumnType.Boolean),
            new ColumnDefinition("category", ColumnType.Text),
            new ColumnDefinition("notes", ColumnType.Text)
        ]));
        // No index is asked for: whether one exists is the engine's business (SC-2), and the
        // storage raises one for a sort over a thing this large the first time it is paged.

        var categories = new[] { "travel", "food", "books", "tools", "fees", "other" };
        const int batch = 5_000;
        for (var from = 0; from < records; from += batch)
        {
            var start = from;
            storage.InUnitOfWork(() =>
            {
                for (var i = start; i < Math.Min(records, start + batch); i++)
                {
                    storage.Create("entries", new Dictionary<string, object?>
                    {
                        ["title"] = $"Entry {i}",
                        ["number"] = (long)i,
                        ["amount"] = (decimal)(i % 1000) / 4,
                        ["day"] = new DateOnly(2024, 1, 1).AddDays(i % 700),
                        ["moment"] = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                        ["done"] = i % 2 == 0,
                        ["category"] = categories[i % categories.Length],
                        ["notes"] = i % 5 == 0 ? "a note about this one" : null
                    });
                }
            });
        }

        var conversation = storage.Conversations.Start("perf fixture");
        for (var i = 0; i < traces; i++)
        {
            var request = storage.Traces.Begin(conversation.Id, $"request {i}");
            var now = DateTimeOffset.UtcNow;
            Ulid? last = null;
            foreach (var name in new[] { "the person", "what was meant", "the answer" })
            {
                var step = new ExecutionStep(Ulid.NewUlid(), request.Id, name, StepStatus.Completed, now, now) { After = last, Input = "x", Output = "y" };
                storage.Traces.Record(step);
                last = step.Id;
            }

            storage.Traces.Move(request.Id, RequestState.Completed, request.Transitions);
        }

        console.WriteLine($"  built {records} records and {traces} traces in {clock.Elapsed.TotalSeconds:0.0}s");
    }

    private static (TimeSpan P50, TimeSpan P95) Measure(int runs, Action work)
    {
        var times = new List<TimeSpan>(runs);
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            work();
            times.Add(clock.Elapsed);
        }

        times.Sort();
        return (times[Math.Clamp((int)Math.Ceiling(0.5 * times.Count) - 1, 0, times.Count - 1)], times[Math.Clamp((int)Math.Ceiling(0.95 * times.Count) - 1, 0, times.Count - 1)]);
    }

    private static string Markdown(IReadOnlyList<Timing> timings, string machine, DateTimeOffset when)
    {
        var text = new StringBuilder();
        text.AppendLine("# Assistant performance report");
        text.AppendLine();
        text.AppendLine($"Measured {when:yyyy-MM-dd HH:mm} on {machine}. Generated by `dotnet run --project TokkDb.Assistant.TokenBudget -- perf`; do not edit by hand. Every timing names its fixture, whether it is cold or warm, the runs behind it, p50 and p95, and the planner path where one is required (NF-6); a time met by a scan where a seek was required fails.");
        text.AppendLine();
        text.AppendLine("| requirement | what | fixture | mode | runs | p50 | p95 | limit | planner path | |");
        text.AppendLine("|---|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (var timing in timings)
        {
            var path = timing.PathRequired is null ? "—" : $"required {timing.PathRequired}; took {timing.PathTaken ?? "?"}";
            text.AppendLine($"| {timing.Requirement} | {timing.What} | {timing.Fixture} | {timing.Mode} | {timing.Runs} | {Ms(timing.P50)} | {Ms(timing.P95)} | {Ms(timing.Limit)} | {path} | {(timing.Met ? "met" : timing.TimeMet ? "**wrong path**" : "**over**")} |");
        }

        text.AppendLine();
        foreach (var timing in timings.Where(static timing => timing.Note.Length > 0)) text.AppendLine($"- {timing.Requirement}, {timing.Mode}: {timing.Note}.");
        text.AppendLine();
        text.AppendLine("Encryption at rest is out of scope (NF-4e): the engine has none, and the database relies on the user's application data location and its file permissions.");
        return text.ToString();
    }

    private static string Ms(TimeSpan time) => time.TotalMilliseconds >= 1000 ? $"{time.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s" : $"{time.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
}
