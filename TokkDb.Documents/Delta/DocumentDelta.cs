using TokkDb.Documents.Values;

namespace TokkDb.Documents.Delta;

//DL-1, DL-4 and DL-6. The difference between two documents: an ordered list of elements, and
//for each array that had an element key declared, how its elements were matched (DL-8).
//
//The order matters. Every element's indices are valid at the moment it is applied, in list
//order, and DocumentDiff says how it arranges that. ApplyTo walks the list in order and Invert
//walks it backwards, so a delta and its inverse agree about every intermediate array.
//
//Stored as an ordinary array value through the existing serializer (DL-6): one item per
//element, preceded by a header object with the array matchings when there are any — a header
//is told from an element by its "arrays" field, which no element has, and a delta with nothing
//to say about arrays leaves it out rather than paying for it on every one-field change. The
//paths inside are stored as segments (DeltaPath.ToDocumentValue), and an absent value is a
//missing field of the element's object, the way an absent field is missing from any document —
//which is exactly the distinction it has to keep.
public sealed class DocumentDelta {
  public static readonly DocumentDelta Empty = new([], []);

  private const string ArraysField = "arrays";
  private const string PathField = "p";
  private const string OperationField = "o";
  private const string OldField = "a";
  private const string NewField = "b";
  private const string MoveToField = "t";
  private const string ModeField = "m";
  private const string KeyField = "k";
  private const string ReasonField = "r";

  private readonly DeltaElement[] _elements;
  private readonly ArrayMatching[] _matchings;

  public DocumentDelta(IEnumerable<DeltaElement> elements, IEnumerable<ArrayMatching>? matchings = null) {
    ArgumentNullException.ThrowIfNull(elements);
    _elements = elements.ToArray();
    _matchings = matchings?.ToArray() ?? [];
  }

  public IReadOnlyList<DeltaElement> Elements => _elements;
  public IReadOnlyList<ArrayMatching> Matchings => _matchings;
  public bool IsEmpty => _elements.Length == 0;

  //How the array at a path was matched: by key only where the delta says so.
  public ArrayMatchingMode MatchingFor(DeltaPath arrayPath) {
    ArgumentNullException.ThrowIfNull(arrayPath);
    return _matchings.FirstOrDefault(matching => matching.Path.Equals(arrayPath))?.Mode
      ?? ArrayMatchingMode.ByPosition;
  }

  //DL-4: the delta that takes b back to a. The elements in reverse order, each inverted.
  public DocumentDelta Invert() {
    var inverted = new DeltaElement[_elements.Length];
    for (var i = 0; i < _elements.Length; i++) {
      inverted[i] = _elements[_elements.Length - 1 - i].Invert();
    }
    return new DocumentDelta(inverted, _matchings);
  }

  //DL-7. The document with this delta applied, as a new value; the one given is not touched.
  //Each element's old value is checked against what the document holds, with CanonicalValue.Equal,
  //and the first mismatch throws DeltaMismatchException naming the path and — when the caller
  //gave one — the version. Nothing partially applied is ever returned: the work is done on a copy
  //that the exception abandons.
  public IDocumentValue ApplyTo(IDocumentValue value, Ulid? version = null) {
    ArgumentNullException.ThrowIfNull(value);
    var root = CanonicalValue.Copy(value);
    foreach (var element in _elements) {
      root = Apply(root, element, version);
    }
    return root;
  }

  private static IDocumentValue Apply(IDocumentValue root, DeltaElement element, Ulid? version) {
    var path = element.Path;
    if (path.IsRoot) {
      if (element.Operation != DeltaOperation.Replace) {
        throw Mismatch(element, version, "a field or an array position", "the root");
      }
      CheckOld(element, version, root);
      return CanonicalValue.Copy(element.NewValue.Value);
    }
    var container = Navigate(root, element, version);
    var last = path.Last;
    switch (element.Operation) {
      case DeltaOperation.Add: {
        var fields = Fields(container, element, version);
        if (fields.TryGetValue(last.Name!, out var existing)) {
          throw Mismatch(element, version, "no field", CanonicalValue.Describe(existing));
        }
        fields[last.Name!] = CanonicalValue.Copy(element.NewValue.Value);
        break;
      }
      case DeltaOperation.Remove: {
        var fields = Fields(container, element, version);
        if (!fields.TryGetValue(last.Name!, out var existing)) {
          throw Mismatch(element, version, CanonicalValue.Describe(element.OldValue.Value), "no field");
        }
        CheckOld(element, version, existing);
        fields.Remove(last.Name!);
        break;
      }
      case DeltaOperation.Replace: {
        if (last.IsField) {
          var fields = Fields(container, element, version);
          if (!fields.TryGetValue(last.Name!, out var existing)) {
            throw Mismatch(element, version, CanonicalValue.Describe(element.OldValue.Value), "no field");
          }
          CheckOld(element, version, existing);
          fields[last.Name!] = CanonicalValue.Copy(element.NewValue.Value);
        } else {
          var array = Array(container, element, version);
          var index = IndexWithin(array, last.Index, element, version, allowEnd: false);
          CheckOld(element, version, array.Values[index]);
          array.Values[index] = CanonicalValue.Copy(element.NewValue.Value);
        }
        break;
      }
      case DeltaOperation.Insert: {
        var array = Array(container, element, version);
        var index = IndexWithin(array, last.Index, element, version, allowEnd: true);
        array.Values = InsertAt(array.Values, index, CanonicalValue.Copy(element.NewValue.Value));
        break;
      }
      case DeltaOperation.RemoveAt: {
        var array = Array(container, element, version);
        var index = IndexWithin(array, last.Index, element, version, allowEnd: false);
        CheckOld(element, version, array.Values[index]);
        array.Values = RemoveAt(array.Values, index);
        break;
      }
      case DeltaOperation.Move: {
        var array = Array(container, element, version);
        var from = IndexWithin(array, last.Index, element, version, allowEnd: false);
        CheckOld(element, version, array.Values[from]);
        var moved = array.Values[from];
        var without = RemoveAt(array.Values, from);
        if (element.MoveTo > without.Length) {
          throw Mismatch(element, version, $"a destination within {without.Length} elements",
            $"destination {element.MoveTo}");
        }
        array.Values = InsertAt(without, element.MoveTo, moved);
        break;
      }
      default:
        throw new InvalidOperationException($"{element.Operation} is not a delta operation.");
    }
    return root;
  }

  //The value the element's last segment applies inside: every segment but the last, followed.
  private static IDocumentValue Navigate(IDocumentValue root, DeltaElement element, Ulid? version) {
    var current = root;
    var segments = element.Path.Segments;
    for (var i = 0; i < segments.Count - 1; i++) {
      var segment = segments[i];
      if (segment.IsField) {
        if (current is not ObjectDocumentValue objectValue) {
          throw Mismatch(element, version, $"an object at {Prefix(element.Path, i)}", CanonicalValue.Describe(current));
        }
        if (!objectValue.Values.TryGetValue(segment.Name!, out var field)) {
          throw Mismatch(element, version, $"a field at {Prefix(element.Path, i + 1)}", "no field");
        }
        current = field;
      } else {
        if (current is not ArrayDocumentValue arrayValue) {
          throw Mismatch(element, version, $"an array at {Prefix(element.Path, i)}", CanonicalValue.Describe(current));
        }
        if (segment.Index >= arrayValue.Values.Length) {
          throw Mismatch(element, version, $"an element at {Prefix(element.Path, i + 1)}",
            $"an array of {arrayValue.Values.Length}");
        }
        current = arrayValue.Values[segment.Index];
      }
    }
    return current;
  }

  private static string Prefix(DeltaPath path, int length) {
    var prefix = DeltaPath.Of(path.Segments.Take(length));
    return prefix.IsRoot ? "the root" : prefix.Render();
  }

  private static Dictionary<string, IDocumentValue> Fields(IDocumentValue container, DeltaElement element,
      Ulid? version) {
    return container is ObjectDocumentValue objectValue
      ? objectValue.Values
      : throw Mismatch(element, version, "an object", CanonicalValue.Describe(container));
  }

  private static ArrayDocumentValue Array(IDocumentValue container, DeltaElement element, Ulid? version) {
    return container is ArrayDocumentValue arrayValue
      ? arrayValue
      : throw Mismatch(element, version, "an array", CanonicalValue.Describe(container));
  }

  private static int IndexWithin(ArrayDocumentValue array, int index, DeltaElement element, Ulid? version,
      bool allowEnd) {
    var limit = allowEnd ? array.Values.Length : array.Values.Length - 1;
    if (index > limit) {
      throw Mismatch(element, version, $"an array of at least {index + (allowEnd ? 0 : 1)}",
        $"an array of {array.Values.Length}");
    }
    return index;
  }

  private static void CheckOld(DeltaElement element, Ulid? version, IDocumentValue found) {
    if (!CanonicalValue.Equal(element.OldValue.Value, found)) {
      throw Mismatch(element, version, CanonicalValue.Describe(element.OldValue.Value), CanonicalValue.Describe(found));
    }
  }

  private static DeltaMismatchException Mismatch(DeltaElement element, Ulid? version, string expected, string found) {
    return new DeltaMismatchException(element.Path, element.Operation, version, expected, found);
  }

  private static IDocumentValue[] InsertAt(IDocumentValue[] values, int index, IDocumentValue value) {
    var result = new IDocumentValue[values.Length + 1];
    System.Array.Copy(values, 0, result, 0, index);
    result[index] = value;
    System.Array.Copy(values, index, result, index + 1, values.Length - index);
    return result;
  }

  private static IDocumentValue[] RemoveAt(IDocumentValue[] values, int index) {
    var result = new IDocumentValue[values.Length - 1];
    System.Array.Copy(values, 0, result, 0, index);
    System.Array.Copy(values, index + 1, result, index, values.Length - index - 1);
    return result;
  }

  //DL-6: the delta as a document value, for the existing serializer.
  public IDocumentValue ToDocumentValue() {
    var withHeader = _matchings.Length > 0 ? 1 : 0;
    var items = new IDocumentValue[_elements.Length + withHeader];
    if (withHeader == 1) {
      items[0] = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
        [ArraysField] = new ArrayDocumentValue(_matchings.Select(WriteMatching).ToArray())
      });
    }
    for (var i = 0; i < _elements.Length; i++) {
      items[i + withHeader] = WriteElement(_elements[i]);
    }
    return new ArrayDocumentValue(items);
  }

  public static DocumentDelta FromDocumentValue(IDocumentValue value) {
    if (value is not ArrayDocumentValue items) {
      throw new FormatException("A stored delta is an array of its elements, with a header in front when it has one.");
    }
    var withHeader = items.Values.Length > 0
      && items.Values[0] is ObjectDocumentValue first
      && first.Values.ContainsKey(ArraysField);
    var matchings = withHeader && ((ObjectDocumentValue)items.Values[0]).Values[ArraysField] is ArrayDocumentValue arrays
      ? arrays.Values.Select(ReadMatching)
      : [];
    return new DocumentDelta(items.Values.Skip(withHeader ? 1 : 0).Select(ReadElement), matchings);
  }

  private static IDocumentValue WriteElement(DeltaElement element) {
    var fields = new Dictionary<string, IDocumentValue> {
      [PathField] = element.Path.ToDocumentValue(),
      [OperationField] = new IntDocumentValue((int)element.Operation)
    };
    if (element.OldValue.IsPresent) {
      fields[OldField] = element.OldValue.Value;
    }
    //A Move carries its value once: both its sides are that value.
    if (element.NewValue.IsPresent && element.Operation != DeltaOperation.Move) {
      fields[NewField] = element.NewValue.Value;
    }
    if (element.Operation == DeltaOperation.Move) {
      fields[MoveToField] = new IntDocumentValue(element.MoveTo);
    }
    return new ObjectDocumentValue(fields);
  }

  private static DeltaElement ReadElement(IDocumentValue value) {
    if (value is not ObjectDocumentValue element) {
      throw new FormatException("A stored delta element is an object.");
    }
    var path = DeltaPath.FromDocumentValue(element.Values.GetValueOrDefault(PathField)
      ?? throw new FormatException("A stored delta element has a path."));
    var operation = (DeltaOperation)ReadInt(element, OperationField);
    var oldValue = element.Values.TryGetValue(OldField, out var old) ? DeltaValue.Of(old) : DeltaValue.Absent;
    var newValue = element.Values.TryGetValue(NewField, out var @new) ? DeltaValue.Of(@new) : DeltaValue.Absent;
    var moveTo = element.Values.ContainsKey(MoveToField) ? ReadInt(element, MoveToField) : -1;
    return DeltaElement.Of(path, operation, oldValue, newValue, moveTo);
  }

  private static IDocumentValue WriteMatching(ArrayMatching matching) {
    var fields = new Dictionary<string, IDocumentValue> {
      [PathField] = matching.Path.ToDocumentValue(),
      [ModeField] = new IntDocumentValue((int)matching.Mode)
    };
    if (matching.Key is not null) {
      fields[KeyField] = new StringDocumentValue(matching.Key);
    }
    if (matching.Reason is not null) {
      fields[ReasonField] = new StringDocumentValue(matching.Reason);
    }
    return new ObjectDocumentValue(fields);
  }

  private static ArrayMatching ReadMatching(IDocumentValue value) {
    if (value is not ObjectDocumentValue matching) {
      throw new FormatException("A stored array matching is an object.");
    }
    return new ArrayMatching(
      DeltaPath.FromDocumentValue(matching.Values.GetValueOrDefault(PathField)
        ?? throw new FormatException("A stored array matching has a path.")),
      (ArrayMatchingMode)ReadInt(matching, ModeField),
      matching.Values.GetValueOrDefault(KeyField) is StringDocumentValue key ? key.Value : null,
      matching.Values.GetValueOrDefault(ReasonField) is StringDocumentValue reason ? reason.Value : null);
  }

  private static int ReadInt(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is IntDocumentValue number
      ? number.Value
      : throw new FormatException($"A stored delta has an int at '{field}'.");
  }

  public override string ToString() {
    return IsEmpty ? "empty delta" : string.Join("; ", _elements.Select(element => element.ToString()));
  }
}
