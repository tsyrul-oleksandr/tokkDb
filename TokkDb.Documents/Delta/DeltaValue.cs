namespace TokkDb.Documents.Delta;

//One side of a delta element: a value, or no value at all.
//
//Absent is not null. A field set to null is a Replace whose new side is a NullDocumentValue; a
//field removed is a Remove whose new side is absent (DL-1, V-2). The two have to stay apart in
//memory as they do in a document, where a field can be missing or can hold null.
public readonly struct DeltaValue : IEquatable<DeltaValue> {
  public static readonly DeltaValue Absent = default;

  private readonly IDocumentValue? _value;

  private DeltaValue(IDocumentValue value) {
    _value = value;
  }

  public static DeltaValue Of(IDocumentValue value) {
    ArgumentNullException.ThrowIfNull(value);
    return new DeltaValue(value);
  }

  public bool IsAbsent => _value is null;
  public bool IsPresent => _value is not null;

  public IDocumentValue Value => _value
    ?? throw new InvalidOperationException("The delta value is absent.");

  //Canonical equality (DL-5): two present values are equal when the document format would
  //store the same bytes for them.
  public bool Equals(DeltaValue other) {
    return IsAbsent ? other.IsAbsent : other.IsPresent && CanonicalValue.Equal(_value, other._value);
  }

  public override bool Equals(object? obj) => obj is DeltaValue other && Equals(other);

  public override int GetHashCode() => IsAbsent ? 0 : CanonicalValue.Hash(_value!);

  public override string ToString() => IsAbsent ? "absent" : CanonicalValue.Describe(_value!);
}
