namespace TokkDb.Benchmarks.Benchmarks;

//How much of the file a record costs. The baseline for NFR-4 later, when delta history has
//to be shown cheaper than full copies.
public class FileSizeGrowthBenchmark : IBenchmark {
  public string Name => "File size growth";
  public string Description => "What a stored record costs on disk, and what the overhead is.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var fileLength = new FileInfo(context.PopulatedDatabasePath).Length;
    using var db = new TokkDbConnection(context.PopulatedDatabasePath);
    db.Load();
    var records = db.Collection(nameof(Publication)).RecordCount;

    //What the documents themselves take, so that the difference is the engine's overhead.
    var payload = db.Entities<Publication>().GetAll()
      .Sum(record => (long)(record.Title.Length + record.Doi.Length +
        (record.Author?.Name.Length ?? 0) + record.Keywords.Sum(keyword => keyword.Name.Length) + 24));

    return [
      new Measurement(Name, "File size", fileLength / 1024.0 / 1024.0, "MiB", Note:
        $"{records:N0} records."),
      new Measurement(Name, "Bytes per record", (double)fileLength / Math.Max(1, records), "bytes"),
      new Measurement(Name, "Overhead over payload", fileLength / (double)Math.Max(1, payload), "x", Note:
        "File size divided by the bytes of user data in it; slot directories, page headers, " +
        "control areas and part-filled pages make up the difference.")
    ];
  }
}
