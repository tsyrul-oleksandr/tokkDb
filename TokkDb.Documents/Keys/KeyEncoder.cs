using System.Buffers.Binary;
using System.Globalization;
using TokkDb.Values;

namespace TokkDb.Documents.Keys;

//D-3: an index key is a byte array whose ordinal comparison is the value's own comparison.
//The B+Tree then never needs to know what it is holding — it compares bytes, and the order
//it keeps is the order the query asked for.
//
//Every encoding here is checked against the CLR type's own CompareTo by the property tests
//in TokkDb.Tests/KeyEncoderTests.cs, including the cases that are easy to get wrong:
//negative numbers, -0.0 and NaN, decimals that are equal at different scales, and Guid,
//whose CompareTo is not the order of its bytes.
public static class KeyEncoder {
  //The encoded payload of a string key, not counting the tag. 8 KiB pages, so this leaves
  //room for roughly thirty keys in a node; anything longer is a prefix and a re-check.
  public const int MaxStringKeyBytes = 256;

  private const ulong SignBit64 = 0x8000000000000000;
  //Ordering the sign of a decimal without a second comparison: below zero, zero, above zero.
  private const byte DecimalNegative = 0x00;
  private const byte DecimalZero = 0x01;
  private const byte DecimalPositive = 0x02;
  //Digits are written one to a byte, biased by one so that zero is free to end the run. A
  //shorter run of digits then sorts before any run that extends it: 0.15 before 0.151.
  private const byte DigitBias = 1;
  private const byte DigitTerminator = 0x00;

  //The entry point an index uses: the column says which type it holds, and a null value of
  //any type encodes as the null key, which sorts before every value.
  public static EncodedKey Encode(ValueTypeEnum type, object? value) {
    if (value == null) {
      return EncodeNull();
    }
    return type switch {
      ValueTypeEnum.Null => EncodeNull(),
      ValueTypeEnum.Boolean => Encode((bool)value),
      ValueTypeEnum.Byte => Encode((byte)value),
      ValueTypeEnum.SByte => Encode((sbyte)value),
      ValueTypeEnum.Short => Encode((short)value),
      ValueTypeEnum.UShort => Encode((ushort)value),
      ValueTypeEnum.Int => Encode((int)value),
      ValueTypeEnum.UInt => Encode((uint)value),
      ValueTypeEnum.Long => Encode((long)value),
      ValueTypeEnum.ULong => Encode((ulong)value),
      ValueTypeEnum.Float => Encode((float)value),
      ValueTypeEnum.Double => Encode((double)value),
      ValueTypeEnum.Decimal => Encode((decimal)value),
      ValueTypeEnum.DateTime => Encode((DateTime)value),
      ValueTypeEnum.TimeSpan => Encode((TimeSpan)value),
      ValueTypeEnum.Guid => Encode((Guid)value),
      ValueTypeEnum.Ulid => Encode((Ulid)value),
      ValueTypeEnum.String => Encode((string)value),
      //An object and an array have no ordering of their own, so there is no encoding that
      //could preserve one. A composite key is a sequence of scalar keys, not an encoded
      //object, and an array is indexed by indexing its elements.
      _ => throw new NotSupportedException(
        $"{type} has no ordering, so it cannot be an index key. Index a scalar column instead.")
    };
  }

  public static EncodedKey EncodeNull() {
    return new EncodedKey([KeyTag.Null]);
  }

  public static EncodedKey Encode(bool value) {
    return new EncodedKey([KeyTag.Boolean, value ? (byte)1 : (byte)0]);
  }

  public static EncodedKey Encode(sbyte value) => EncodeSigned(value);
  public static EncodedKey Encode(short value) => EncodeSigned(value);
  public static EncodedKey Encode(int value) => EncodeSigned(value);
  public static EncodedKey Encode(long value) => EncodeSigned(value);

  public static EncodedKey Encode(byte value) => EncodeUnsigned(value);
  public static EncodedKey Encode(ushort value) => EncodeUnsigned(value);
  public static EncodedKey Encode(uint value) => EncodeUnsigned(value);
  public static EncodedKey Encode(ulong value) => EncodeUnsigned(value);

  public static EncodedKey Encode(float value) => Encode((double)value);

  public static EncodedKey Encode(double value) {
    return new EncodedKey(TaggedUInt64(KeyTag.FloatingPoint, OrderableBits(value)));
  }

  //DateTime.CompareTo is Ticks.CompareTo and nothing else, so the ticks are the key and Kind
  //is not part of it. Two instants that differ only in Kind are one key, exactly as they are
  //one value to the type.
  public static EncodedKey Encode(DateTime value) {
    return new EncodedKey(TaggedUInt64(KeyTag.DateTime, Flip(value.Ticks)));
  }

  public static EncodedKey Encode(TimeSpan value) {
    return new EncodedKey(TaggedUInt64(KeyTag.TimeSpan, Flip(value.Ticks)));
  }

  //Guid.CompareTo does not compare the bytes ToByteArray() returns: the first three fields
  //are stored little-endian there, so encoding that array would sort the index in an order
  //no caller uses. The big-endian form is the one whose unsigned byte order is CompareTo's
  //order — a runtime detail, since the first three fields were once compared signed, so the
  //property test checks it against the running runtime rather than trusting this comment.
  public static EncodedKey Encode(Guid value) {
    var key = new byte[1 + 16];
    key[0] = KeyTag.Guid;
    value.TryWriteBytes(key.AsSpan(1), bigEndian: true, out _);
    return new EncodedKey(key);
  }

  //D-1's payoff: a Ulid's bytes are already its order, timestamp first, so an index on a
  //record identity takes its inserts at the right-hand edge of the tree.
  public static EncodedKey Encode(Ulid value) {
    var key = new byte[1 + 16];
    key[0] = KeyTag.Ulid;
    value.TryWriteBytes(key.AsSpan(1));
    return new EncodedKey(key);
  }

  //Sign, then magnitude as an exponent and a digit run — the shape a decimal has to be put
  //in for byte comparison to work at all, because 1.5 and 1.50 are one value at two scales
  //and a comparison of their stored forms would not know it.
  public static EncodedKey Encode(decimal value) {
    if (value == 0m) {
      return new EncodedKey([KeyTag.Decimal, DecimalZero]);
    }
    var negative = value < 0m;
    var digits = SignificantDigits(Math.Abs(value), out var exponent);
    var key = new byte[1 + 1 + 2 + digits.Length + 1];
    key[0] = KeyTag.Decimal;
    key[1] = negative ? DecimalNegative : DecimalPositive;
    //A biased short: a larger exponent is a larger magnitude, for every exponent a decimal
    //can have (-27 through 29).
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(2), (ushort)(exponent + short.MaxValue + 1));
    for (var i = 0; i < digits.Length; i++) {
      key[4 + i] = (byte)(digits[i] - '0' + DigitBias);
    }
    key[^1] = DigitTerminator;
    if (negative) {
      //A larger magnitude is a smaller number below zero, so everything after the sign byte
      //is complemented — the terminator with it, so it still ends the run from underneath.
      for (var i = 2; i < key.Length; i++) {
        key[i] = (byte)~key[i];
      }
    }
    return new EncodedKey(key);
  }

  //UTF-16 code units big-endian, which is exactly what string.CompareOrdinal compares. UTF-8
  //would be a byte or two shorter for Latin text and identical for Cyrillic, but its byte
  //order is code point order, and that disagrees with ordinal comparison wherever a
  //surrogate pair meets a character above U+E000.
  public static EncodedKey Encode(string value) {
    var normalized = KeyNormalization.Normalize(value);
    var units = Math.Min(normalized.Length, MaxStringKeyBytes / sizeof(char));
    var key = new byte[1 + units * sizeof(char)];
    key[0] = KeyTag.String;
    for (var i = 0; i < units; i++) {
      BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1 + i * sizeof(char)), normalized[i]);
    }
    //Truncating at a whole code unit is what keeps the prefix ordered: a cut through the
    //middle of one would leave a high byte comparing as if it were a character.
    return new EncodedKey(key, units < normalized.Length, IsFolded: true);
  }

  private static EncodedKey EncodeSigned(long value) {
    return new EncodedKey(TaggedUInt64(KeyTag.SignedInteger, Flip(value)));
  }

  private static EncodedKey EncodeUnsigned(ulong value) {
    return new EncodedKey(TaggedUInt64(KeyTag.UnsignedInteger, value));
  }

  //Two's complement puts the negatives above the positives when the bytes are read unsigned.
  //Flipping the sign bit moves them back below, and nothing else about the order changes.
  private static ulong Flip(long value) {
    return (ulong)value ^ SignBit64;
  }

  //IEEE 754 is already ordered within a sign; the negatives are ordered backwards, so they
  //are complemented, and the positives get the sign bit set to lift them above.
  //
  //NaN is not ordered by IEEE at all, but double.CompareTo puts every NaN below negative
  //infinity, so it is mapped below the smallest value this produces.
  //
  //-0.0 and +0.0 are two bit patterns that CompareTo calls equal, so they have to be one
  //key for the same reason 1.5 and 1.50 do: an index that sorted them apart would hold two
  //entries for one value, and a unique index over them would admit both.
  private static ulong OrderableBits(double value) {
    if (double.IsNaN(value)) {
      return 0;
    }
    if (value == 0.0) {
      return SignBit64;
    }
    var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
    return (bits & SignBit64) != 0 ? ~bits : bits | SignBit64;
  }

  private static byte[] TaggedUInt64(byte tag, ulong value) {
    var key = new byte[1 + sizeof(ulong)];
    key[0] = tag;
    BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), value);
    return key;
  }

  //The digits of a positive decimal with no leading and no trailing zero, and the exponent
  //that puts them back: value == 0.<digits> * 10^exponent.
  //
  //Taken from the invariant text because decimal.ToString is exact and never exponential —
  //the digits it prints are the value, trailing zeros of the scale included, which is what
  //has to be stripped for 1.50 and 1.5 to reach the same key.
  private static string SignificantDigits(decimal value, out int exponent) {
    var text = value.ToString(CultureInfo.InvariantCulture);
    var point = text.IndexOf('.');
    var integerLength = point < 0 ? text.Length : point;
    var digits = point < 0 ? text : text.Remove(point, 1);
    var leadingZeros = 0;
    while (leadingZeros < digits.Length && digits[leadingZeros] == '0') {
      leadingZeros++;
    }
    exponent = integerLength - leadingZeros;
    return digits[leadingZeros..].TrimEnd('0');
  }
}
