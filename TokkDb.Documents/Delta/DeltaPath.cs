using System.Globalization;
using System.Text;
using TokkDb.Documents.Values;

namespace TokkDb.Documents.Delta;

//One step of a DeltaPath: a field of an object, or a position in an array.
public readonly struct DeltaSegment : IEquatable<DeltaSegment> {
  private DeltaSegment(string? name, int index) {
    Name = name;
    Index = index;
  }

  public static DeltaSegment Field(string name) {
    ArgumentNullException.ThrowIfNull(name);
    return new DeltaSegment(name, -1);
  }

  public static DeltaSegment At(int index) {
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    return new DeltaSegment(null, index);
  }

  //The field name, or null for an index segment.
  public string? Name { get; }

  //The array position, or -1 for a field segment.
  public int Index { get; }

  public bool IsIndex => Name is null;
  public bool IsField => Name is not null;

  public bool Equals(DeltaSegment other) {
    return IsIndex
      ? other.IsIndex && Index == other.Index
      : other.IsField && string.Equals(Name, other.Name, StringComparison.Ordinal);
  }

  public override bool Equals(object? obj) => obj is DeltaSegment other && Equals(other);

  public override int GetHashCode() => IsIndex ? Index : StringComparer.Ordinal.GetHashCode(Name!);

  public override string ToString() => IsIndex ? $"[{Index}]" : DeltaPath.RenderName(Name!);
}

//DL-2. Where a delta element applies: a sequence of field-name and array-index segments.
//
//Kept as segments, and rendered for people as a.b[3].c. A field name that contains a dot, a
//bracket, a quote or a backslash — or is empty — is rendered in double quotes with the quote and
//the backslash escaped, so that a field named "a.b" cannot be read as two fields and one named
//"x[0]" cannot be read as an index. The rendering is for messages and displays; storage keeps the
//segments (ToDocumentValue), because a path stored as its rendering would have to be parsed back
//and would depend on the quoting rule never changing.
public sealed class DeltaPath : IEquatable<DeltaPath> {
  public static readonly DeltaPath Root = new([]);

  private readonly DeltaSegment[] _segments;

  private DeltaPath(DeltaSegment[] segments) {
    _segments = segments;
  }

  public static DeltaPath Of(params DeltaSegment[] segments) {
    return segments.Length == 0 ? Root : new DeltaPath((DeltaSegment[])segments.Clone());
  }

  public static DeltaPath Of(IEnumerable<DeltaSegment> segments) {
    return Of(segments.ToArray());
  }

  public IReadOnlyList<DeltaSegment> Segments => _segments;
  public int Length => _segments.Length;
  public bool IsRoot => _segments.Length == 0;

  public DeltaSegment Last => IsRoot
    ? throw new InvalidOperationException("The root path has no last segment.")
    : _segments[^1];

  public DeltaPath Parent => IsRoot
    ? throw new InvalidOperationException("The root path has no parent.")
    : Of(_segments[..^1]);

  public DeltaPath Field(string name) => Append(DeltaSegment.Field(name));

  public DeltaPath At(int index) => Append(DeltaSegment.At(index));

  public DeltaPath Append(DeltaSegment segment) {
    var segments = new DeltaSegment[_segments.Length + 1];
    _segments.CopyTo(segments, 0);
    segments[^1] = segment;
    return new DeltaPath(segments);
  }

  //The path with its index segments left out: what a declaration means when it names "the
  //array at authors", whichever element of an outer array the document is in (DL-8).
  public DeltaPath WithoutIndices() {
    return _segments.All(segment => segment.IsField) ? this : Of(_segments.Where(segment => segment.IsField));
  }

  public string Render() {
    var text = new StringBuilder();
    foreach (var segment in _segments) {
      if (segment.IsIndex) {
        text.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
      } else {
        if (text.Length > 0) {
          text.Append('.');
        }
        text.Append(RenderName(segment.Name!));
      }
    }
    return text.ToString();
  }

  internal static string RenderName(string name) {
    if (name.Length > 0 && name.All(character => character is not ('.' or '[' or ']' or '"' or '\\'))) {
      return name;
    }
    return '"' + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
  }

  //The reverse of Render, for a path typed by a person. Storage never goes through here.
  public static DeltaPath Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var segments = new List<DeltaSegment>();
    var position = 0;
    var atStart = true;
    while (position < text.Length) {
      var character = text[position];
      if (character == '[') {
        var close = text.IndexOf(']', position);
        if (close < 0) {
          throw new FormatException($"Path '{text}' has an index without a closing bracket at {position}.");
        }
        if (!int.TryParse(text.AsSpan(position + 1, close - position - 1), NumberStyles.None,
              CultureInfo.InvariantCulture, out var index)) {
          throw new FormatException($"Path '{text}' has an index that is not a number at {position}.");
        }
        segments.Add(DeltaSegment.At(index));
        position = close + 1;
        atStart = false;
        continue;
      }
      if (character == '.') {
        if (atStart) {
          throw new FormatException($"Path '{text}' starts with a dot.");
        }
        position++;
        if (position >= text.Length || text[position] is '.' or '[') {
          throw new FormatException($"Path '{text}' has a dot that no field name follows, at {position - 1}.");
        }
        segments.Add(ReadName(text, ref position));
        continue;
      }
      if (!atStart) {
        throw new FormatException($"Path '{text}' expects a dot or a bracket at {position}.");
      }
      segments.Add(ReadName(text, ref position));
      atStart = false;
    }
    return Of(segments);
  }

  private static DeltaSegment ReadName(string text, ref int position) {
    if (text[position] == '"') {
      var name = new StringBuilder();
      position++;
      while (true) {
        if (position >= text.Length) {
          throw new FormatException($"Path '{text}' has a quoted name that never closes.");
        }
        var character = text[position++];
        if (character == '"') {
          return DeltaSegment.Field(name.ToString());
        }
        if (character == '\\') {
          if (position >= text.Length) {
            throw new FormatException($"Path '{text}' ends inside an escape.");
          }
          character = text[position++];
        }
        name.Append(character);
      }
    }
    var start = position;
    while (position < text.Length && text[position] is not ('.' or '[')) {
      if (text[position] is ']' or '"' or '\\') {
        throw new FormatException($"Path '{text}' has an unquoted name with a special character at {position}.");
      }
      position++;
    }
    if (position == start) {
      throw new FormatException($"Path '{text}' has an empty field name at {start}.");
    }
    return DeltaSegment.Field(text[start..position]);
  }

  //Stored as an array of the segments themselves: a string for a field, an int for an index.
  //The type byte tells the two apart, so a field named "3" and the index 3 never meet.
  public IDocumentValue ToDocumentValue() {
    return new ArrayDocumentValue(_segments
      .Select(IDocumentValue (segment) => segment.IsIndex
        ? new IntDocumentValue(segment.Index)
        : new StringDocumentValue(segment.Name!))
      .ToArray());
  }

  public static DeltaPath FromDocumentValue(IDocumentValue value) {
    if (value is not ArrayDocumentValue array) {
      throw new FormatException("A stored path is an array of segments.");
    }
    return Of(array.Values.Select(segment => segment switch {
      StringDocumentValue name => DeltaSegment.Field(name.Value),
      IntDocumentValue index => DeltaSegment.At(index.Value),
      _ => throw new FormatException($"A stored path segment is a string or an int, not {segment.Type}.")
    }));
  }

  public bool Equals(DeltaPath? other) {
    return other is not null && _segments.AsSpan().SequenceEqual(other._segments);
  }

  public override bool Equals(object? obj) => obj is DeltaPath other && Equals(other);

  public override int GetHashCode() {
    var hash = new HashCode();
    foreach (var segment in _segments) {
      hash.Add(segment);
    }
    return hash.ToHashCode();
  }

  public override string ToString() => Render();
}
