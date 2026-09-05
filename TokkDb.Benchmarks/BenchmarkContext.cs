namespace TokkDb.Benchmarks;

public class BenchmarkContext {
  private readonly List<string> _databases = [];

  public BenchmarkContext(int recordCount, int collectionCount, string workingDirectory) {
    RecordCount = recordCount;
    CollectionCount = collectionCount;
    WorkingDirectory = workingDirectory;
    Directory.CreateDirectory(workingDirectory);
  }

  public int RecordCount { get; }
  public int CollectionCount { get; }
  public string WorkingDirectory { get; }

  //The record-filled database the first benchmark builds and the later ones read.
  public string PopulatedDatabasePath { get; set; } = string.Empty;

  //Identities spread evenly through that database, kept as it is built so that a lookup
  //benchmark does not have to scan the collection to find something to look up.
  public List<Ulid> SampleRecordIds { get; } = [];

  //How often the primary index had to split while that database was built. Counted on the
  //connection that did the building: a tree opened later is a fresh view of the same pages
  //and has no memory of what it took to put them there.
  public long IndexLeafSplits { get; set; }

  public string CreateDatabasePath(string name) {
    var path = Path.Combine(WorkingDirectory, $"{name}.db");
    Remove(path);
    _databases.Add(path);
    return path;
  }

  public void Cleanup() {
    foreach (var path in _databases) {
      Remove(path);
    }
  }

  private static void Remove(string path) {
    foreach (var candidate in new[] { path, path + ".wal", path + ".lock" }) {
      if (File.Exists(candidate)) {
        File.Delete(candidate);
      }
    }
  }
}

public interface IBenchmark {
  string Name { get; }
  string Description { get; }
  IEnumerable<Measurement> Run(BenchmarkContext context);
}
