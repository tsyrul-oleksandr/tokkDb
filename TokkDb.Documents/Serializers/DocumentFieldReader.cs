using System.Text;
using TokkDb.Buffer;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Serializers;

//Reads one named field out of a serialized document without parsing the rest of it.
//
//Phase 6 needs this because a predicate names one or two columns and a record carries every
//column it has: deserializing the whole document to look at "Year" costs a dictionary, a
//string per key and a value object per field, all of it thrown away for the records that do
//not match. The format is self-describing — a type byte in front of every value — so an
//unwanted field can be stepped over by reading its length rather than its content.
public static class DocumentFieldReader {

  //The field to look for, with its name already encoded. A query asks for the same column of
  //every record it examines, so the UTF-8 of the name is worth encoding once rather than per
  //record — and the comparison then happens against the stored bytes, with no string
  //allocated for the fields that do not match.
  public readonly struct FieldName {
    private readonly byte[] _utf8;

    public FieldName(string name) {
      Name = name;
      _utf8 = Encoding.UTF8.GetBytes(name);
    }

    public string Name { get; }

    internal bool Matches(BufferSlice buffer, int index, int length) {
      return _utf8 != null && length == _utf8.Length && buffer.AsReadOnlySpan(index, length).SequenceEqual(_utf8);
    }
  }

  //The value of one field of the object that starts at position, or null when the object has
  //no such field. Only the wanted field is materialised; the others are stepped over.
  public static IDocumentValue Read(BufferSlice buffer, int position, FieldName field) {
    var type = (ValueTypeEnum)buffer.ReadByte(position);
    if (type != ValueTypeEnum.Object) {
      return null;
    }
    position += TypesConstants.ByteByteSize;
    var count = buffer.ReadInt(position, out var countBytes);
    position += countBytes;
    for (var i = 0; i < count; i++) {
      var keyLength = buffer.ReadInt(position, out var keyLengthBytes);
      var keyStart = position + keyLengthBytes;
      position = keyStart + keyLength;
      if (field.Matches(buffer, keyStart, keyLength)) {
        return ReadValue(buffer, position);
      }
      position = SkipValue(buffer, position);
    }
    return null;
  }

  //Every field of the object, in stored order. The planner does not use it; the tests that
  //prove the skipping arithmetic agrees with the writer do.
  public static IEnumerable<string> FieldNames(BufferSlice buffer, int position) {
    if ((ValueTypeEnum)buffer.ReadByte(position) != ValueTypeEnum.Object) {
      yield break;
    }
    position += TypesConstants.ByteByteSize;
    var count = buffer.ReadInt(position, out var countBytes);
    position += countBytes;
    for (var i = 0; i < count; i++) {
      var key = buffer.ReadString(position, out var keyBytes);
      position = SkipValue(buffer, position + keyBytes);
      yield return key;
    }
  }

  private static IDocumentValue ReadValue(BufferSlice buffer, int position) {
    return new BufferReader(buffer, position).Read();
  }

  //Where the value that starts at position ends. This is the writer's layout read backwards,
  //so the two have to agree: a value written by ValueUtilities.Write is a type byte and then
  //whatever WriteValue put down for that type.
  private static int SkipValue(BufferSlice buffer, int position) {
    var type = (ValueTypeEnum)buffer.ReadByte(position);
    position += TypesConstants.ByteByteSize;
    switch (type) {
      case ValueTypeEnum.Null:
        return position;
      case ValueTypeEnum.Boolean:
        return position + TypesConstants.BooleanByteSize;
      case ValueTypeEnum.Int:
        return position + TypesConstants.IntByteSize;
      case ValueTypeEnum.UInt:
        return position + TypesConstants.UIntByteSize;
      case ValueTypeEnum.Ulid:
        return position + TypesConstants.UlidByteSize;
      case ValueTypeEnum.String: {
        var length = buffer.ReadInt(position, out var lengthBytes);
        return position + lengthBytes + length;
      }
      case ValueTypeEnum.Array: {
        var count = buffer.ReadInt(position, out var countBytes);
        position += countBytes;
        for (var i = 0; i < count; i++) {
          position = SkipValue(buffer, position);
        }
        return position;
      }
      case ValueTypeEnum.Object: {
        var count = buffer.ReadInt(position, out var countBytes);
        position += countBytes;
        for (var i = 0; i < count; i++) {
          var keyLength = buffer.ReadInt(position, out var keyLengthBytes);
          position = SkipValue(buffer, position + keyLengthBytes + keyLength);
        }
        return position;
      }
      default:
        //The same types ValueUtilities refuses to read. Skipping one would mean guessing a
        //width the writer never wrote, which would silently misread every field after it.
        throw new NotSupportedException(
          $"A value of type {type} has no stored form to step over. " +
          $"The document format writes {nameof(ValueTypeEnum.Long)}, {nameof(ValueTypeEnum.Decimal)}, " +
          $"{nameof(ValueTypeEnum.DateTime)} and {nameof(ValueTypeEnum.Guid)} as text.");
    }
  }
}
