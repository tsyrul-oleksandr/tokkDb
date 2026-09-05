using System.Diagnostics;

namespace TokkDb.Benchmarks.Benchmarks;

//NFR-2: single-record lookup under 10 ms. That target assumes an indexed field, and there
//are no indexes before Phase 5, so what this measures is the sequential scan the engine
//falls back on. It is the number Phase 5 has to beat.
public class LookupLatencyBenchmark : IBenchmark {
  private const int Lookups = 50;

  public string Name => "Lookup latency";
  public string Description => "Lookup of one record by a non-key field, today a full scan.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    using var db = new TokkDbConnection(context.PopulatedDatabasePath);
    db.Load();
    var entities = db.Entities<Publication>();

    //Spread the targets over the file so the scan length averages out.
    var wanted = Enumerable.Range(0, Lookups)
      .Select(i => $"10.1000/tokkdb.{i * Math.Max(1, context.RecordCount / Lookups):D8}")
      .ToList();

    _ = entities.GetAll().FirstOrDefault(record => record.Doi == wanted[0]);

    var stopwatch = Stopwatch.StartNew();
    var found = 0;
    foreach (var doi in wanted) {
      if (entities.GetAll().FirstOrDefault(record => record.Doi == doi) is not null) {
        found++;
      }
    }
    stopwatch.Stop();

    var readsBefore = db.PageReadCount;
    _ = entities.GetAll().FirstOrDefault(record => record.Doi == wanted[^1]);
    var readsPerLookup = db.PageReadCount - readsBefore;

    return [
      new Measurement(Name, "Lookup by DOI", stopwatch.Elapsed.TotalMilliseconds / wanted.Count, "ms",
        "NFR-2", 10, $"Sequential scan of {context.RecordCount:N0} records; no index exists before Phase 5. " +
        $"{found} of {wanted.Count} targets found."),
      new Measurement(Name, "Pages read per lookup", readsPerLookup, "pages", Note:
        "Every data page of the collection, which is what an index has to replace.")
    ];
  }
}
