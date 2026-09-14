namespace TokkDb.Documents.Delta;

//DL-7. A delta element's old value, or the shape it expects, is not what the document holds.
//Names the path, and the version when the caller gave one, so that reconstruction can say which
//stored version first fails rather than which byte.
public sealed class DeltaMismatchException : Exception {
  public DeltaMismatchException(DeltaPath path, DeltaOperation operation, Ulid? version, string expected,
      string found)
    : base(Describe(path, operation, version, expected, found)) {
    Path = path;
    Operation = operation;
    Version = version;
    Expected = expected;
    Found = found;
  }

  public DeltaPath Path { get; }
  public DeltaOperation Operation { get; }
  public Ulid? Version { get; }
  public string Expected { get; }
  public string Found { get; }

  private static string Describe(DeltaPath path, DeltaOperation operation, Ulid? version, string expected,
      string found) {
    var at = path.IsRoot ? "the root" : path.Render();
    var of = version is null ? "" : $" of version {version}";
    return $"The delta{of} does not match the document: {operation} at {at} expected {expected} but found {found}.";
  }
}
