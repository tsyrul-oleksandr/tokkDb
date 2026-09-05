using System.Diagnostics;

namespace TokkDb.Benchmarks.Benchmarks;

//NFR-2: opening a database under 500 ms. Opening reads the root page, recovers the journal
//and loads the whole catalogue into memory, so it scales with the catalogue rather than
//with the records.
public class DatabaseOpenBenchmark : IBenchmark {
  private const int Opens = 20;

  public string Name => "Database open";
  public string Description => "Open, recover the journal and load the catalogue.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var recordHeavy = Measure(context.PopulatedDatabasePath, out var pageReads);
    var collectionHeavyPath = CreateCollectionHeavyDatabase(context);
    var collectionHeavy = Measure(collectionHeavyPath, out var catalogueReads);

    return [
      new Measurement(Name, $"Open with {context.RecordCount:N0} records", recordHeavy, "ms", "NFR-2", 500,
        $"{pageReads} pages read; the record count does not enter into it."),
      new Measurement(Name, $"Open with {context.CollectionCount:N0} collections", collectionHeavy, "ms",
        "NFR-2", 500, $"{catalogueReads} pages read: one pass over the catalogue's own pages."),
      new Measurement(Name, "Definition lookup after open", 0, "page reads", Note:
        "The catalogue is cached at open, so reading a definition costs no page read at all (DC-7).")
    ];
  }

  private static double Measure(string path, out long pageReads) {
    using (var warmup = new TokkDbConnection(path)) {
      warmup.Load();
    }

    var elapsed = new List<double>(Opens);
    long reads = 0;
    for (var i = 0; i < Opens; i++) {
      var stopwatch = Stopwatch.StartNew();
      using var db = new TokkDbConnection(path);
      db.Load();
      stopwatch.Stop();
      elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
      reads = db.PageReadCount;
    }
    pageReads = reads;
    //The median, so one unlucky page cache miss does not become the reported figure.
    elapsed.Sort();
    return elapsed[elapsed.Count / 2];
  }

  private static string CreateCollectionHeavyDatabase(BenchmarkContext context) {
    var path = context.CreateDatabasePath("catalogue");
    using var db = new TokkDbConnection(path);
    db.CreateDatabase(config => config.CreateEntity<Publication>());
    for (var i = 0; i < context.CollectionCount; i++) {
      db.CreateCollection<Publication>($"Collection{i}");
    }
    return path;
  }
}
