using System.Diagnostics;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.Benchmarks.Benchmarks;

//NF-5: what a write costs at the device under None and under KeepVersions, with 0 and 3
//secondary indexes — pages written, journal bytes, file growth, and the latency of a bulk
//unit of work and of a durable one — over three workloads: an assistant-like import of
//10 000 rows of 12 columns in one unit of work, small edits to wide records, and a mix with
//deletes. Secure release is not built yet (step 7.2) and is measured there.
public class WriteAmplificationBenchmark : IBenchmark {
  private const int ImportRows = 10_000;
  private const int WideRecords = 500;
  private const int WideEdits = 1_000;
  private const int DurableWrites = 100;

  public string Name => "Write amplification";

  public string Description =>
    "Pages written, journal bytes, file growth and latency per operation under None and KeepVersions, with 0 and 3 indexes.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var measurements = new List<Measurement>();
    foreach (var policy in new[] { RetentionPolicy.None, RetentionPolicy.KeepVersions }) {
      foreach (var indexes in new[] { 0, 3 }) {
        var label = $"{policy}, {indexes} indexes";
        measurements.AddRange(Import(context, policy, indexes, label));
        measurements.AddRange(WideEditsAndMix(context, policy, indexes, label));
      }
    }
    return measurements;
  }

  private static List<ColumnDescriptor> ImportColumns() {
    return Enumerable.Range(0, 12)
      .Select(i => new ColumnDescriptor($"column{i:D2}", i % 4 == 0 ? ValueTypeEnum.Int : ValueTypeEnum.String)).ToList();
  }

  private static Dictionary<string, IDocumentValue> Row(int record, int version) {
    var row = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    for (var i = 0; i < 12; i++) {
      row[$"column{i:D2}"] = i % 4 == 0
        ? new IntDocumentValue(record * 100 + i + version)
        : new StringDocumentValue($"row {record} column {i} version {version}");
    }
    return row;
  }

  private static (TokkDbConnection Db, CountingDiskManager Disk) Open(BenchmarkContext context, string name,
      RetentionPolicy policy, int indexes, List<ColumnDescriptor> columns) {
    var disk = new CountingDiskManager(context.CreateDatabasePath(name));
    var db = new TokkDbConnection(disk);
    db.Load();
    db.CreateCollection("W", columns);
    foreach (var column in columns.Where(column => column.Type == ValueTypeEnum.String).Take(indexes)) {
      db.CreateIndex("W", column.Name);
    }
    db.SetRetentionPolicy("W", policy, dropHistory: policy == RetentionPolicy.None);
    return (db, disk);
  }

  private IEnumerable<Measurement> Import(BenchmarkContext context, RetentionPolicy policy, int indexes, string label) {
    var (db, disk) = Open(context, $"import-{policy}-{indexes}", policy, indexes, ImportColumns());
    using (db) {
      var entities = db.Entities(new FieldMapSerializer(), "W");
      var before = new FileInfo(disk.FilePath).Length;
      disk.ResetCounters();
      var bulk = Stopwatch.StartNew();
      db.InTransaction(() => {
        for (var record = 0; record < ImportRows; record++) {
          entities.Insert(Row(record, 0));
        }
      });
      bulk.Stop();
      var growth = new FileInfo(disk.FilePath).Length - before;
      yield return new Measurement(Name, $"Import: bulk insert, {label}", bulk.Elapsed.TotalMilliseconds * 1000 / ImportRows, "µs",
        "NFR-4", Note: $"{ImportRows:N0} rows of 12 columns in one unit of work: {disk.PagesWritten:N0} pages written, " +
          $"{disk.JournalBytes / 1024.0:N0} KiB journalled, {disk.Flushes} flushes.");
      yield return new Measurement(Name, $"Import: pages per row, {label}", disk.PagesWritten / (double)ImportRows, "pages");
      yield return new Measurement(Name, $"Import: file growth per row, {label}", growth / (double)ImportRows, "bytes");

      disk.ResetCounters();
      var durable = Stopwatch.StartNew();
      for (var record = 0; record < DurableWrites; record++) {
        entities.Insert(Row(ImportRows + record, 0));
      }
      durable.Stop();
      yield return new Measurement(Name, $"Import: durable insert, {label}", durable.Elapsed.TotalMilliseconds / DurableWrites, "ms",
        "NFR-2", 5, $"{DurableWrites} rows, one unit of work each: {disk.PagesWritten / (double)DurableWrites:0.#} pages and " +
          $"{disk.JournalBytes / 1024.0 / DurableWrites:0.#} KiB of journal per row.");
    }
  }

  private IEnumerable<Measurement> WideEditsAndMix(BenchmarkContext context, RetentionPolicy policy, int indexes, string label) {
    //Twelve declared of the fifty fields each record carries: a descriptor has to fit a page.
    var columns = Enumerable.Range(0, 12)
      .Select(i => new ColumnDescriptor($"field{i:D3}", i % 3 == 0 ? ValueTypeEnum.String : ValueTypeEnum.Int)).ToList();
    var random = new Random(3);
    Dictionary<string, IDocumentValue> Wide(int record, int version) {
      var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
      for (var i = 0; i < 50; i++) {
        document[$"field{i:D3}"] = i % 3 == 0 ? new StringDocumentValue($"record {record} field {i}") : new IntDocumentValue(i * 7 + version);
      }
      return document;
    }
    var (db, disk) = Open(context, $"wide-{policy}-{indexes}", policy, indexes, columns);
    using (db) {
      var entities = db.Entities(new FieldMapSerializer(), "W");
      var ids = new List<Ulid>();
      var current = new List<Dictionary<string, IDocumentValue>>();
      db.InTransaction(() => {
        for (var record = 0; record < WideRecords; record++) {
          current.Add(Wide(record, 0));
          ids.Add(entities.Insert(current[record]));
        }
      });

      //Small edits: one field of one record per write, in one unit of work.
      var before = new FileInfo(disk.FilePath).Length;
      disk.ResetCounters();
      var edits = Stopwatch.StartNew();
      db.InTransaction(() => {
        for (var edit = 0; edit < WideEdits; edit++) {
          var record = random.Next(WideRecords);
          var next = new Dictionary<string, IDocumentValue>(current[record], StringComparer.Ordinal);
          var field = random.Next(50);
          next[$"field{field:D3}"] = field % 3 == 0 ? new StringDocumentValue($"edited {edit}") : new IntDocumentValue(edit);
          current[record] = next;
          entities.Update(ids[record], next);
        }
      });
      edits.Stop();
      yield return new Measurement(Name, $"Wide edits: bulk update, {label}", edits.Elapsed.TotalMilliseconds * 1000 / WideEdits, "µs",
        "NFR-4", Note: $"{WideEdits:N0} one-field edits to 50-field records in one unit of work: {disk.PagesWritten:N0} pages written, " +
          $"{disk.JournalBytes / 1024.0:N0} KiB journalled, {(new FileInfo(disk.FilePath).Length - before) / 1024.0:N0} KiB of file growth.");
      yield return new Measurement(Name, $"Wide edits: pages per edit, {label}", disk.PagesWritten / (double)WideEdits, "pages");

      disk.ResetCounters();
      var durable = Stopwatch.StartNew();
      for (var edit = 0; edit < DurableWrites; edit++) {
        var record = edit % WideRecords;
        var next = new Dictionary<string, IDocumentValue>(current[record], StringComparer.Ordinal);
        next["field001"] = new IntDocumentValue(100_000 + edit);
        current[record] = next;
        entities.Update(ids[record], next);
      }
      durable.Stop();
      yield return new Measurement(Name, $"Wide edits: durable update, {label}", durable.Elapsed.TotalMilliseconds / DurableWrites, "ms",
        "NFR-2", 5, $"{DurableWrites} edits, one unit of work each: {disk.PagesWritten / (double)DurableWrites:0.#} pages and " +
          $"{disk.JournalBytes / 1024.0 / DurableWrites:0.#} KiB of journal per edit.");

      //A mix: inserts, edits and deletes in one unit of work.
      before = new FileInfo(disk.FilePath).Length;
      disk.ResetCounters();
      var mix = Stopwatch.StartNew();
      var operations = 0;
      db.InTransaction(() => {
        for (var round = 0; round < 200; round++) {
          var record = random.Next(ids.Count);
          if (round % 4 == 0 && ids.Count > 100) {
            entities.Delete(ids[record]);
            ids.RemoveAt(record);
            current.RemoveAt(record);
          } else if (round % 4 == 1) {
            var document = Wide(WideRecords + round, 0);
            current.Add(document);
            ids.Add(entities.Insert(document));
          } else {
            var next = new Dictionary<string, IDocumentValue>(current[record], StringComparer.Ordinal);
            next["field002"] = new IntDocumentValue(round);
            current[record] = next;
            entities.Update(ids[record], next);
          }
          operations++;
        }
      });
      mix.Stop();
      yield return new Measurement(Name, $"Mix with deletes: per operation, {label}", mix.Elapsed.TotalMilliseconds * 1000 / operations, "µs",
        Note: $"{operations} operations, a quarter of them deletes and a quarter inserts, in one unit of work: {disk.PagesWritten:N0} pages written, " +
          $"{disk.JournalBytes / 1024.0:N0} KiB journalled, {(new FileInfo(disk.FilePath).Length - before) / 1024.0:N0} KiB of file growth.");
    }
  }
}
