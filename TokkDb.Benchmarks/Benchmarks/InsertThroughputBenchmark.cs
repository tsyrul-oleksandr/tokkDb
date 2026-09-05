using System.Diagnostics;

namespace TokkDb.Benchmarks.Benchmarks;

//NFR-2: insert of a structured record under 5 ms, LLM time excluded. One insert is one
//transaction, so this measures the whole commit protocol, journal flushes included.
public class InsertThroughputBenchmark : IBenchmark {
  public string Name => "Insert throughput";
  public string Description => "One record per transaction through the full commit protocol.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var path = context.CreateDatabasePath("insert");
    context.PopulatedDatabasePath = path;

    using var db = new TokkDbConnection(path);
    db.CreateDatabase(config => config.CreateEntity<Publication>());
    var entities = db.Entities<Publication>();

    //A short warm-up keeps first-call JIT out of the measured window.
    for (var i = 0; i < Math.Min(50, context.RecordCount); i++) {
      entities.Insert(Publication.Numbered(-i - 1));
    }

    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < context.RecordCount; i++) {
      entities.Insert(Publication.Numbered(i));
    }
    stopwatch.Stop();

    var perInsert = stopwatch.Elapsed.TotalMilliseconds / context.RecordCount;
    return [
      new Measurement(Name, "Insert of one record", perInsert, "ms", "NFR-2", 5,
        "One transaction per record: three fsyncs each (journal images, database file, commit record)."),
      new Measurement(Name, "Throughput", context.RecordCount / stopwatch.Elapsed.TotalSeconds, "records/s"),
      new Measurement(Name, "Total for the run", stopwatch.Elapsed.TotalSeconds, "s", Note:
        $"{context.RecordCount:N0} records.")
    ];
  }
}
