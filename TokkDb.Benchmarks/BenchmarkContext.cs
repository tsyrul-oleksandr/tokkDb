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
