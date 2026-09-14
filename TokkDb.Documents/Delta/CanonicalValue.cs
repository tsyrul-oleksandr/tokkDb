using System.Globalization;
using TokkDb.Buffer;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Delta;

//DL-5 and DL-4. Values compared by what the document format stores for them — never by CLR
//equality, which says 1.50m equals 1.5m, and never by index-key equality, which says Int 5
//equals Long 5. The canonical form of an object writes its fields in ordinal order, so two
//objects holding the same fields in a different order are one value; an array's order is its
//own. A DateTime differing only in Kind stores different bytes (DateTime.ToBinary) and is a
//different value.
//
//Only parsed values can be compared: a BufferedObjectValue is a view over a page and cannot
//enumerate its fields, and asking for its bytes throws as its WriteValue does.
public static class CanonicalValue {
  public static bool Equal(IDocumentValue? a, IDocumentValue? b) {
    if (ReferenceEquals(a, b)) {
      return true;
    }
    if (a is null || b is null || a.Type != b.Type) {
      return false;
    }
    switch (a) {
      case ObjectDocumentValue objectA when b is ObjectDocumentValue objectB: {
        if (objectA.Values.Count != objectB.Values.Count) {
          return false;
        }
        foreach (var (key, value) in objectA.Values) {
          if (!objectB.Values.TryGetValue(key, out var other) || !Equal(value, other)) {
            return false;
          }
        }
        return true;
      }
      case ArrayDocumentValue arrayA when b is ArrayDocumentValue arrayB: {
        if (arrayA.Values.Length != arrayB.Values.Length) {
          return false;
        }
        for (var i = 0; i < arrayA.Values.Length; i++) {
          if (!Equal(arrayA.Values[i], arrayB.Values[i])) {
            return false;
          }
        }
        return true;
      }
      default:
        return Bytes(a).AsSpan().SequenceEqual(Bytes(b));
    }
  }

  //The stored bytes of a value: its type byte and what WriteValue puts down, with an object's
  //fields in ordinal order. Measured first and then written, as StoredRecordUtilities does, so
  //no buffer is ever too small.
  public static byte[] Bytes(IDocumentValue value) {
    ArgumentNullException.ThrowIfNull(value);
    var counting = new BufferWriter(new CountingBufferSlice());
    Write(counting, value);
    var bytes = new byte[counting.Position];
    Write(new BufferWriter(new BufferSlice(bytes)), value);
    return bytes;
  }

  //The canonical bytes of every element of an array in one buffer, with the offset each element
  //starts at (and a final offset at the end), so that aligning two arrays costs two passes over
  //each and one allocation rather than one per element (DL-3).
  public static (byte[] Bytes, int[] Offsets) ElementBytes(ArrayDocumentValue array) {
    ArgumentNullException.ThrowIfNull(array);
    var offsets = new int[array.Values.Length + 1];
    var counting = new BufferWriter(new CountingBufferSlice());
    for (var i = 0; i < array.Values.Length; i++) {
      offsets[i] = counting.Position;
      Write(counting, array.Values[i]);
    }
    offsets[^1] = counting.Position;
    var bytes = new byte[counting.Position];
    var writer = new BufferWriter(new BufferSlice(bytes));
    foreach (var element in array.Values) {
      Write(writer, element);
    }
    return (bytes, offsets);
  }

  public static void Write(BufferWriter writer, IDocumentValue value) {
    switch (value) {
      case ObjectDocumentValue objectValue:
        writer.WriteByte((byte)ValueTypeEnum.Object);
        writer.WriteInt(objectValue.Values.Count);
        foreach (var key in objectValue.Values.Keys.OrderBy(key => key, StringComparer.Ordinal)) {
          writer.WriteString(key);
          Write(writer, objectValue.Values[key]);
        }
        break;
      case ArrayDocumentValue arrayValue:
        writer.WriteByte((byte)ValueTypeEnum.Array);
        writer.WriteInt(arrayValue.Values.Length);
        foreach (var element in arrayValue.Values) {
          Write(writer, element);
        }
        break;
      default:
        writer.Write(value);
        break;
    }
  }

  //A hash that agrees with Equal, over the canonical bytes.
  public static int Hash(IDocumentValue value) {
    return Hash(Bytes(value));
  }

  //FNV-1a, folded to an int. Any function of the bytes would do; this one needs no state.
  public static int Hash(ReadOnlySpan<byte> bytes) {
    var hash = 14695981039346656037UL;
    foreach (var b in bytes) {
      hash = (hash ^ b) * 1099511628211UL;
    }
    return (int)(hash ^ (hash >> 32));
  }

  //A value nothing else refers to. Objects and arrays are rebuilt; a scalar goes through its
  //bytes, which is the one copy that is right for every scalar type without knowing them.
  public static IDocumentValue Copy(IDocumentValue value) {
    ArgumentNullException.ThrowIfNull(value);
    switch (value) {
      case ObjectDocumentValue objectValue: {
        var values = new Dictionary<string, IDocumentValue>(objectValue.Values.Count, StringComparer.Ordinal);
        foreach (var (key, field) in objectValue.Values) {
          values[key] = Copy(field);
        }
        return new ObjectDocumentValue(values);
      }
      case ArrayDocumentValue arrayValue: {
        var values = new IDocumentValue[arrayValue.Values.Length];
        for (var i = 0; i < values.Length; i++) {
          values[i] = Copy(arrayValue.Values[i]);
        }
        return new ArrayDocumentValue(values);
      }
      case NullDocumentValue:
        return new NullDocumentValue();
      default:
        return new BufferReader(new BufferSlice(Bytes(value))).Read();
    }
  }

  //A short description for messages: the type, and the value when it is a scalar.
  public static string Describe(IDocumentValue? value) {
    return value switch {
      null => "absent",
      NullDocumentValue => "null",
      StringDocumentValue text => text.Value.Length > 40
        ? $"\"{text.Value[..40]}…\" ({text.Value.Length} characters)"
        : $"\"{text.Value}\"",
      IntDocumentValue number => $"Int {number.Value.ToString(CultureInfo.InvariantCulture)}",
      UIntDocumentValue number => $"UInt {number.Value.ToString(CultureInfo.InvariantCulture)}",
      LongDocumentValue number => $"Long {number.Value.ToString(CultureInfo.InvariantCulture)}",
      DecimalDocumentValue number => $"Decimal {number.Value.ToString(CultureInfo.InvariantCulture)}",
      BooleanDocumentValue flag => flag.Value ? "true" : "false",
      DateTimeDocumentValue moment => $"DateTime {moment.Value.ToString("O", CultureInfo.InvariantCulture)}",
      GuidDocumentValue identifier => $"Guid {identifier.Value:D}",
      UlidDocumentValue identifier => $"Ulid {identifier.Value}",
      ObjectDocumentValue objectValue => $"an object with {objectValue.Values.Count} fields",
      ArrayDocumentValue arrayValue => $"an array of {arrayValue.Values.Length}",
      _ => value.Type.ToString()
    };
  }
}
