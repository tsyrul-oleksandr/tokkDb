using System.Diagnostics;

namespace TokkDb.Benchmarks.Benchmarks;

//DC-6, and the read/write trade-off §2.3 asserts. An index is paid for on every write and
//repaid on every read that can use it, and the only way to say whether that is worth it is
//to measure both halves against the same collection.
//
//Both sides of the write are reported, because they answer different questions. One
//transaction per record is what a caller sees, and there the three fsyncs of the commit
//protocol are so much larger than index maintenance that they hide it. A bulk load in one
//transaction takes the fsyncs out and shows what the indexes themselves cost.
public class IndexMaintenanceBenchmark : IBenchmark {
  private static readonly string[] Columns = ["Doi", "Title", "Year", "Institution", "DocumentType"];
  private static readonly int[] IndexCounts = [0, 1, 3, 5];

  //Enough for the tree to be more than one page and for the averages to settle, and small
  //enough that four configurations of it do not take the hour the durable path would.
  private const int BulkRecords = 20_000;

  //One transaction each, so this is the number a caller would see. Kept small because every
  //one of them is three fsyncs.
  private const int DurableRecords = 300;

  private const int Lookups = 200;

  public string Name => "Index maintenance";

  public string Description =>
    "Insert throughput against the number of secondary indexes, and what those indexes buy back.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var measurements = new List<Measurement>();
    foreach (var indexCount in IndexCounts) {
      measurements.AddRange(Measure(context, indexCount));
    }
    return measurements;
  }

  private IEnumerable<Measurement> Measure(BenchmarkContext context, int indexCount) {
    var path = context.CreateDatabasePath($"indexes-{indexCount}");
    using var db = new TokkDbConnection(path);
    db.CreateDatabase(config => config.CreateEntity<Publication>());
    foreach (var column in Columns.Take(indexCount)) {
      db.CreateIndex(nameof(Publication), column);
    }
    var entities = db.Entities<Publication>();
    var label = indexCount == 1 ? "1 index" : $"{indexCount} indexes";

    //Warm up outside the measured window, and out of the range the lookups use.
    db.InTransaction(() => {
      for (var i = 0; i < 200; i++) {
        entities.Insert(Publication.Numbered(-i - 1));
      }
    });

    var bulk = Stopwatch.StartNew();
    db.InTransaction(() => {
      for (var i = 0; i < BulkRecords; i++) {
        entities.Insert(Publication.Numbered(i));
      }
    });
    bulk.Stop();

    var durable = Stopwatch.StartNew();
    for (var i = 0; i < DurableRecords; i++) {
      entities.Insert(Publication.Numbered(BulkRecords + i));
    }
    durable.Stop();

    var measurements = new List<Measurement> {
      new(Name, $"Bulk insert, {label}", bulk.Elapsed.TotalMilliseconds * 1000 / BulkRecords, "µs",
        "DC-6", Note: $"{BulkRecords:N0} records in one transaction, so the three fsyncs of a commit " +
          "are paid once and what is left is the work of the write itself."),
      new(Name, $"Durable insert, {label}", durable.Elapsed.TotalMilliseconds / DurableRecords, "ms",
        "NFR-2", 5, $"{DurableRecords:N0} records, one transaction each."),
      new(Name, $"File size, {label}", new FileInfo(path).Length / 1024.0 / 1024.0, "MiB", Note:
        $"{BulkRecords + DurableRecords + 200:N0} records and their indexes.")
    };

    //The other half of the trade-off. Without an index the same lookup is the scan of every
    //data page that the lookup benchmark measures.
    if (indexCount > 0) {
      var wanted = Enumerable.Range(0, Lookups)
        .Select(i => $"10.1000/tokkdb.{i * (BulkRecords / Lookups):D8}").ToList();
      _ = entities.GetBy("Doi", wanted[0]).ToList();

      var lookup = Stopwatch.StartNew();
      var found = wanted.Count(doi => entities.GetBy("Doi", doi).Any());
      lookup.Stop();

      var readsBefore = db.PageReadCount;
      _ = entities.GetBy("Doi", wanted[^1]).ToList();
      var pagesRead = db.PageReadCount - readsBefore;

      measurements.Add(new Measurement(Name, $"Lookup by DOI, {label}",
        lookup.Elapsed.TotalMilliseconds / wanted.Count, "ms", "NFR-2", 10,
        $"Through the index on Doi. {found} of {wanted.Count} targets found, {pagesRead} pages for one."));
    }
    return measurements;
  }
}
