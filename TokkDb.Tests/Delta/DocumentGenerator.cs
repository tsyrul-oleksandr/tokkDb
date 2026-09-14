using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;

namespace TokkDb.Tests.Delta;

//Random documents and random edits of them, for the property tests of DL-4 and DL-8.
//
//Every document is at least three levels deep and holds an array of objects; every implemented
//value type appears, nulls included, and fields come and go. In keyed mode the objects of the
//"items" arrays carry an "id" that edits keep distinct, except now and then on purpose, so that
//the fallback to positions is exercised as well as the key matching.
internal sealed class DocumentGenerator {
  private readonly Random _random;
  private readonly bool _keyed;
  private int _nextId = 1000;

  public DocumentGenerator(Random random, bool keyed) {
    _random = random;
    _keyed = keyed;
  }

  public ObjectDocumentValue Document() {
    var fields = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      //The guaranteed depth: an object in an object in the document, with a scalar inside.
      ["nested"] = Values.Obj(("inner", Values.Obj(("leaf", Scalar()), ("other", Scalar())))),
      ["items"] = ItemArray(_random.Next(0, 6))
    };
    var count = _random.Next(2, 7);
    for (var i = 0; i < count; i++) {
      fields[FieldName()] = Value(depth: 0);
    }
    return new ObjectDocumentValue(fields);
  }

  private string FieldName() {
    var names = new[] { "a", "b", "c", "title", "year", "a.b", "x[0]", "\"q\"", "", "Ünïcode", "long field name" };
    return names[_random.Next(names.Length)];
  }

  private IDocumentValue Value(int depth) {
    if (depth < 3 && _random.Next(4) == 0) {
      return _random.Next(2) == 0 ? Object(depth + 1) : Array(depth + 1);
    }
    return Scalar();
  }

  private ObjectDocumentValue Object(int depth) {
    var fields = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    var count = _random.Next(0, 5);
    for (var i = 0; i < count; i++) {
      fields[FieldName()] = Value(depth);
    }
    return new ObjectDocumentValue(fields);
  }

  private ArrayDocumentValue Array(int depth) {
    if (_random.Next(2) == 0) {
      return ItemArray(_random.Next(0, 5));
    }
    var count = _random.Next(0, 6);
    var items = new IDocumentValue[count];
    for (var i = 0; i < count; i++) {
      items[i] = Value(depth);
    }
    return new ArrayDocumentValue(items);
  }

  private ArrayDocumentValue ItemArray(int count) {
    var items = new IDocumentValue[count];
    for (var i = 0; i < count; i++) {
      items[i] = Item();
    }
    return new ArrayDocumentValue(items);
  }

  private ObjectDocumentValue Item() {
    var fields = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["id"] = Values.Int(_nextId++),
      ["name"] = Scalar()
    };
    if (_random.Next(3) == 0) {
      fields["tags"] = Array(3);
    }
    return new ObjectDocumentValue(fields);
  }

  public IDocumentValue Scalar() {
    switch (_random.Next(12)) {
      case 0: return new NullDocumentValue();
      case 1: return new IntDocumentValue(_random.Next(-5, 5));
      case 2: return new UIntDocumentValue((uint)_random.Next(0, 5));
      case 3: return new LongDocumentValue(_random.NextInt64(-5, 5));
      case 4: return new DecimalDocumentValue(_random.Next(0, 3) switch { 0 => 1.5m, 1 => 1.50m, _ => -2.25m });
      case 5: return new BooleanDocumentValue(_random.Next(2) == 0);
      case 6: return new StringDocumentValue(new[] { "", "a", "b", "Кирилиця", "same", "same" }[_random.Next(6)]);
      case 7: return new DateTimeDocumentValue(new DateTime(638_000_000_000_000_000 + _random.Next(3) * 600_000_000L,
        _random.Next(2) == 0 ? DateTimeKind.Utc : DateTimeKind.Unspecified));
      case 8: return new GuidDocumentValue(new Guid(_random.Next(3), 0, 0, new byte[8]));
      case 9: return new UlidDocumentValue(new Ulid(new byte[] { (byte)_random.Next(3), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
      case 10: return new IntDocumentValue(5);
      default: return new LongDocumentValue(5);
    }
  }

  //A copy of the document with a handful of random edits: values replaced, fields added and
  //removed, array elements inserted, removed, moved and changed inside, subtrees swapped out.
  public IDocumentValue Mutate(IDocumentValue document) {
    var copy = CanonicalValue.Copy(document);
    var edits = _random.Next(1, 7);
    for (var i = 0; i < edits; i++) {
      copy = Edit(copy, 0);
    }
    return copy;
  }

  private IDocumentValue Edit(IDocumentValue value, int depth) {
    switch (value) {
      case ObjectDocumentValue objectValue: {
        var keys = objectValue.Values.Keys.ToList();
        var choice = _random.Next(6);
        if (choice == 0 || keys.Count == 0) {
          objectValue.Values[FieldName()] = Value(depth + 1);
        } else if (choice == 1) {
          objectValue.Values.Remove(keys[_random.Next(keys.Count)]);
        } else if (choice == 2) {
          objectValue.Values[keys[_random.Next(keys.Count)]] = Value(depth + 1);
        } else {
          var key = keys[_random.Next(keys.Count)];
          objectValue.Values[key] = Edit(objectValue.Values[key], depth + 1);
        }
        return objectValue;
      }
      case ArrayDocumentValue arrayValue: {
        var items = arrayValue.Values.ToList();
        var choice = _random.Next(7);
        if (choice == 0 || items.Count == 0) {
          items.Insert(_random.Next(items.Count + 1), items.Count > 0 && items[0] is ObjectDocumentValue { Values: var fields }
            && fields.ContainsKey("id") ? Item() : Value(depth + 1));
        } else if (choice == 1) {
          items.RemoveAt(_random.Next(items.Count));
        } else if (choice == 2) {
          var from = _random.Next(items.Count);
          var moved = items[from];
          items.RemoveAt(from);
          items.Insert(_random.Next(items.Count + 1), moved);
        } else if (choice == 3 && _keyed && items[0] is ObjectDocumentValue { Values: var first } && first.ContainsKey("id")) {
          //A duplicated key, so that the diff has to fall back to positions.
          items.Add(CanonicalValue.Copy(items[_random.Next(items.Count)]));
        } else {
          var index = _random.Next(items.Count);
          items[index] = Edit(items[index], depth + 1);
        }
        arrayValue.Values = items.ToArray();
        return arrayValue;
      }
      default:
        return Scalar();
    }
  }
}
