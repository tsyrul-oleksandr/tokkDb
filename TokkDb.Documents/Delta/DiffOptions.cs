namespace TokkDb.Documents.Delta;

//DL-8 and V-2. What DocumentDiff.Compute is told beyond the two documents: the element key of
//each array that has one, by the array's path with indices left out, so that "authors" names the
//authors array of every element of an outer array as well as the top-level one.
public sealed class DiffOptions {
  public static readonly DiffOptions None = new(new Dictionary<DeltaPath, string>());

  private readonly Dictionary<DeltaPath, string> _elementKeys;

  private DiffOptions(Dictionary<DeltaPath, string> elementKeys) {
    _elementKeys = elementKeys;
  }

  public IReadOnlyDictionary<DeltaPath, string> ElementKeys => _elementKeys;

  //A copy of these options with one more key. The path is taken without its indices.
  public DiffOptions WithElementKey(DeltaPath arrayPath, string key) {
    ArgumentNullException.ThrowIfNull(arrayPath);
    if (string.IsNullOrEmpty(key)) {
      throw new ArgumentException("An element key names a field.", nameof(key));
    }
    var elementKeys = new Dictionary<DeltaPath, string>(_elementKeys) {
      [arrayPath.WithoutIndices()] = key
    };
    return new DiffOptions(elementKeys);
  }

  //The common case: an array column of the document, keyed by a field of its elements.
  public DiffOptions WithElementKey(string columnName, string key) {
    return WithElementKey(DeltaPath.Root.Field(columnName), key);
  }

  public string? ElementKeyFor(DeltaPath arrayPath) {
    return _elementKeys.Count == 0 ? null : _elementKeys.GetValueOrDefault(arrayPath.WithoutIndices());
  }
}
