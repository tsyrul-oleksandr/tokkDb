using System.Diagnostics;
using System.Globalization;
using TokkDb.Benchmarks;
using TokkDb.Benchmarks.Benchmarks;

//Every number in the report is formatted the same way whatever machine produced it.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

//The measurement harness behind the experimental chapter. Every phase adds benchmarks here;
//the numbers land in docs/benchmarks.md, run by run.
var recordCount = ReadIntOption(args, "--records", 5000);
var collectionCount = ReadIntOption(args, "--collections", 500);
var phase = ReadOption(args, "--phase", "Phase 2 — journal and recovery");
var reportPath = ReadOption(args, "--report", FindRepositoryPath("docs/benchmarks.md"));
var workingDirectory = ReadOption(args, "--work", Path.Combine(Path.GetTempPath(), "tokkdb-benchmarks"));

var context = new BenchmarkContext(recordCount, collectionCount, workingDirectory);
IBenchmark[] benchmarks = [
  new InsertThroughputBenchmark(),
  new LookupLatencyBenchmark(),
  new DatabaseOpenBenchmark(),
  new FileSizeGrowthBenchmark()
];

Console.WriteLine($"TokkDb benchmarks — {recordCount:N0} records, {collectionCount:N0} collections");
Console.WriteLine();

var measurements = new List<Measurement>();
var total = Stopwatch.StartNew();
try {
  foreach (var benchmark in benchmarks) {
    Console.WriteLine($"  running {benchmark.Name}...");
    measurements.AddRange(benchmark.Run(context));
  }
} finally {
  context.Cleanup();
}
total.Stop();

Console.WriteLine();
MarkdownReport.WriteConsole(measurements);
Console.WriteLine();
Console.WriteLine($"Finished in {total.Elapsed.TotalSeconds:N1} s.");

var written = MarkdownReport.Write(reportPath, phase, context, benchmarks, measurements);
Console.WriteLine($"Report written to {written}");

var missed = measurements.Count(measurement => measurement.MeetsTarget == false);
if (missed > 0) {
  //Not a failure of the run: a target that is not met yet is the point of a baseline.
  Console.WriteLine($"{missed} measurement(s) do not meet their target yet.");
}
return 0;

static int ReadIntOption(string[] args, string name, int fallback) {
  return int.TryParse(ReadOption(args, name, string.Empty), out var value) ? value : fallback;
}

static string ReadOption(string[] args, string name, string fallback) {
  var index = Array.IndexOf(args, name);
  return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

//Lets the tool be run from anywhere in the repository and still find docs/.
static string FindRepositoryPath(string relativePath) {
  var directory = new DirectoryInfo(AppContext.BaseDirectory);
  while (directory is not null) {
    if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) {
      return Path.Combine(directory.FullName, relativePath);
    }
    directory = directory.Parent;
  }
  return relativePath;
}
