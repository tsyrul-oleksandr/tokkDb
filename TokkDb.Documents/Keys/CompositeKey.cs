namespace TokkDb.Documents.Keys;

//D-3: a secondary index stores the composite key (encodedValue, recordId), so a column with
//repeated values needs no posting list hanging off each of them — the record identity makes
//every entry distinct, and all the entries for one value sit together in the tree.
//
//Concatenating the two is not enough on its own. A string key is variable length, and if one
//value's key is a prefix of another's the identity that follows it would be compared against
//the rest of the longer value: "ab" + id would fall on the wrong side of "abc" + id. The
//value is therefore terminated rather than merely appended to.
public static class CompositeKey {
  //0x00 cannot end the value, because a UTF-16 code unit contains one for every character
  //below U+0100 — "A" encodes as 0x00 0x41. So a real 0x00 is written as 0x00 0xFF and the
  //terminator is the one pair that can never occur inside a value, 0x00 0x00. It sorts below
  //every escaped byte, which is what makes a shorter value sort before a longer one that
  //starts with it.
  private const byte Escape = 0x00;
  private const byte EscapedZero = 0xFF;
  private const byte Terminator = 0x00;

  //For a caller that has a value and an identity rather than two encoded keys.
  public static byte[] Encode<T>(T value, Ulid recordId) where T : struct {
    return Create(KeyEncoder.Encode(ValueTypeOf(value), value), KeyEncoder.Encode(recordId));
  }

  private static TokkDb.Values.ValueTypeEnum ValueTypeOf<T>(T value) where T : struct {
    return value switch {
      int => TokkDb.Values.ValueTypeEnum.Int,
      uint => TokkDb.Values.ValueTypeEnum.UInt,
      bool => TokkDb.Values.ValueTypeEnum.Boolean,
      Ulid => TokkDb.Values.ValueTypeEnum.Ulid,
      _ => throw new NotSupportedException($"{typeof(T).Name} is not an index key type.")
    };
  }

  public static byte[] Create(EncodedKey value, EncodedKey recordId) {
    var prefix = ValuePrefix(value);
    var key = new byte[prefix.Length + recordId.Bytes.Length];
    prefix.CopyTo(key, 0);
    recordId.Bytes.CopyTo(key, prefix.Length);
    return key;
  }

  //Everything the entries for one value share, and therefore the lower bound of the range
  //holding all of them: the terminated value with no identity after it, which sorts below
  //every composite key built from it.
  public static byte[] ValuePrefix(EncodedKey value) {
    var zeros = 0;
    foreach (var b in value.Bytes) {
      if (b == Escape) {
        zeros++;
      }
    }
    var prefix = new byte[value.Bytes.Length + zeros + 2];
    var position = 0;
    foreach (var b in value.Bytes) {
      prefix[position++] = b;
      if (b == Escape) {
        prefix[position++] = EscapedZero;
      }
    }
    prefix[position] = Escape;
    prefix[position + 1] = Terminator;
    return prefix;
  }

  //The upper bound of that range, exclusive. The prefix ends in the terminator pair, so
  //raising its last byte lands above every key that carries an identity after it and below
  //every key belonging to the next value.
  public static byte[] AboveValuePrefix(EncodedKey value) {
    var bound = ValuePrefix(value);
    for (var i = bound.Length - 1; i >= 0; i--) {
      if (bound[i] != byte.MaxValue) {
        bound[i]++;
        return bound[..(i + 1)];
      }
    }
    //Every byte was 0xFF, which the terminator makes impossible; an unbounded range is still
    //the right answer if it ever happened.
    return null;
  }

  //The identity half of a composite key: the fixed-width tail the value was terminated to
  //make findable.
  public static Ulid ReadRecordId(byte[] compositeKey) {
    return KeyEncoder.DecodeUlid(compositeKey.AsSpan(compositeKey.Length - KeyEncoder.UlidKeyByteSize));
  }
}
