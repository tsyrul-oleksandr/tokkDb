using TokkDb.Documents.Keys;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//D-3. The property every test here checks is the same one: encode two values, compare the
//two byte arrays ordinally, and get the answer the type's own CompareTo would have given.
//That is the whole contract a B+Tree relies on, so it is checked over random pairs rather
//than over examples somebody thought of.
public class KeyEncoderTests {
  //Fixed, so a failure is a failure anyone can reproduce rather than one that shows up in
  //one run out of fifty.
  private const int Seed = 20260905;
  private const int RandomPairs = 2000;

  //Random pairs find the ordinary cases; the awkward ones are worth naming, so every
  //boundary value of a type is also crossed with every other.
  private static void AssertOrdersAgree<T>(Func<Random, T> next, Func<T, EncodedKey> encode,
      Comparison<T> compare, params T[] boundaries) {
    foreach (var left in boundaries) {
      foreach (var right in boundaries) {
        AssertPairAgrees(left, right, encode, compare);
      }
    }
    var random = new Random(Seed);
    for (var i = 0; i < RandomPairs; i++) {
      AssertPairAgrees(next(random), next(random), encode, compare);
    }
  }

  private static void AssertPairAgrees<T>(T left, T right, Func<T, EncodedKey> encode,
      Comparison<T> compare) {
    var expected = Math.Sign(compare(left, right));
    var actual = Math.Sign(KeyComparer.Compare(encode(left).Bytes, encode(right).Bytes));
    Assert.True(expected == actual,
      $"{left} vs {right}: the type says {expected}, the encoded keys say {actual}.");
  }

  [Fact]
  public void NullSortsBelowEveryValueOfEveryType() {
    var nullKey = KeyEncoder.EncodeNull().Bytes;
    var values = new[] {
      KeyEncoder.Encode(false).Bytes,
      KeyEncoder.Encode(long.MinValue).Bytes,
      KeyEncoder.Encode(ulong.MinValue).Bytes,
      KeyEncoder.Encode(double.NaN).Bytes,
      KeyEncoder.Encode(decimal.MinValue).Bytes,
      KeyEncoder.Encode(DateTime.MinValue).Bytes,
      KeyEncoder.Encode(TimeSpan.MinValue).Bytes,
      KeyEncoder.Encode(Guid.Empty).Bytes,
      KeyEncoder.Encode(Ulid.Empty).Bytes,
      KeyEncoder.Encode(string.Empty).Bytes
    };
    foreach (var value in values) {
      Assert.True(KeyComparer.Compare(nullKey, value) < 0);
    }
  }

  [Fact]
  public void SignedIntegersOrderWithTheirSignBitFlipped() {
    AssertOrdersAgree(r => r.NextInt64(), KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      long.MinValue, long.MinValue + 1, -1L, 0L, 1L, long.MaxValue - 1, long.MaxValue);
  }

  [Fact]
  public void IntsOrderAndEncodeAsTheLongsTheyWidenInto() {
    AssertOrdersAgree(r => (int)r.NextInt64(int.MinValue, int.MaxValue + 1L),
      KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      int.MinValue, -1, 0, 1, int.MaxValue);
    //A column widened from Int to Long keeps the index it already has, because the widening
    //does not change a single key.
    Assert.Equal(KeyEncoder.Encode(-29L).Bytes, KeyEncoder.Encode(-29).Bytes);
    Assert.Equal(KeyEncoder.Encode((long)int.MinValue).Bytes, KeyEncoder.Encode(int.MinValue).Bytes);
  }

  [Fact]
  public void ShortsAndSBytesOrderAsSignedIntegers() {
    AssertOrdersAgree(r => (short)r.Next(short.MinValue, short.MaxValue + 1),
      KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      short.MinValue, (short)-1, (short)0, (short)1, short.MaxValue);
    AssertOrdersAgree(r => (sbyte)r.Next(sbyte.MinValue, sbyte.MaxValue + 1),
      KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      sbyte.MinValue, (sbyte)-1, (sbyte)0, (sbyte)1, sbyte.MaxValue);
  }

  [Fact]
  public void UnsignedIntegersOrderAsTheirBytes() {
    AssertOrdersAgree(r => (ulong)r.NextInt64(), KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      ulong.MinValue, 1UL, (ulong)long.MaxValue, ulong.MaxValue - 1, ulong.MaxValue);
    AssertOrdersAgree(r => (uint)r.Next(), KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      uint.MinValue, 1U, uint.MaxValue);
    AssertOrdersAgree(r => (ushort)r.Next(ushort.MaxValue + 1), KeyEncoder.Encode,
      (a, b) => a.CompareTo(b), ushort.MinValue, (ushort)1, ushort.MaxValue);
    AssertOrdersAgree(r => (byte)r.Next(256), KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      byte.MinValue, (byte)1, byte.MaxValue);
  }

  [Fact]
  public void BooleansOrderFalseBeforeTrue() {
    AssertOrdersAgree(r => r.Next(2) == 1, KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      false, true);
  }

  //Not double.CompareTo's easy cases: every NaN belongs below negative infinity, and -0.0
  //is equal to +0.0 rather than below it, so the two bit patterns have to be one key.
  [Fact]
  public void DoublesOrderIncludingNaNAndNegativeZero() {
    AssertOrdersAgree(NextDouble, KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      double.NaN, double.NegativeInfinity, double.MinValue, -1.0, -double.Epsilon, -0.0, 0.0,
      double.Epsilon, 1.0, double.MaxValue, double.PositiveInfinity);
    Assert.Equal(KeyEncoder.Encode(0.0).Bytes, KeyEncoder.Encode(-0.0).Bytes);
    Assert.Equal(KeyEncoder.Encode(double.NaN).Bytes,
      KeyEncoder.Encode(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001))).Bytes);
  }

  [Fact]
  public void FloatsOrderAndEncodeAsTheDoublesTheyWidenInto() {
    AssertOrdersAgree(r => (float)NextDouble(r), KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      float.NaN, float.NegativeInfinity, float.MinValue, -1.0f, -0.0f, 0.0f, 1.0f,
      float.MaxValue, float.PositiveInfinity);
    Assert.Equal(KeyEncoder.Encode(0.5d).Bytes, KeyEncoder.Encode(0.5f).Bytes);
  }

  [Fact]
  public void DecimalsOrderAcrossSignsScalesAndMagnitudes() {
    AssertOrdersAgree(NextDecimal, KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      decimal.MinValue, -1.5m, -1m, -0.0000001m, 0m, 0.0000001m, 0.15m, 0.151m, 1m, 1.5m,
      1.50m, 15m, decimal.MaxValue);
  }

  //1.5 and 1.50 are one value at two scales. An encoding that kept the scale would sort
  //them apart and a unique index would let both in.
  [Fact]
  public void DecimalsEqualAtDifferentScalesEncodeIdentically() {
    Assert.Equal(KeyEncoder.Encode(1.5m).Bytes, KeyEncoder.Encode(1.50m).Bytes);
    Assert.Equal(KeyEncoder.Encode(-1.5m).Bytes, KeyEncoder.Encode(-1.500m).Bytes);
    Assert.Equal(KeyEncoder.Encode(0m).Bytes, KeyEncoder.Encode(0.0000m).Bytes);
    Assert.Equal(KeyEncoder.Encode(0m).Bytes, KeyEncoder.Encode(-0.0m).Bytes);
  }

  [Fact]
  public void DateTimesOrderByTicks() {
    AssertOrdersAgree(r => new DateTime(r.NextInt64(DateTime.MaxValue.Ticks + 1)),
      KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      DateTime.MinValue, new DateTime(1, 1, 2), new DateTime(2026, 9, 5), DateTime.MaxValue);
    //DateTime.CompareTo ignores Kind, so the key does too, or two values the type calls
    //equal would occupy two places in the index.
    Assert.Equal(
      KeyEncoder.Encode(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc)).Bytes,
      KeyEncoder.Encode(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Local)).Bytes);
  }

  [Fact]
  public void TimeSpansOrderAcrossZero() {
    AssertOrdersAgree(r => new TimeSpan(r.NextInt64()), KeyEncoder.Encode,
      (a, b) => a.CompareTo(b),
      TimeSpan.MinValue, TimeSpan.FromDays(-1), TimeSpan.Zero, TimeSpan.FromTicks(1),
      TimeSpan.MaxValue);
  }

  //The one whose CompareTo is not the order of the bytes ToByteArray() hands back: its first
  //three fields are stored little-endian there, so that array would sort wrong within each
  //of them. What CompareTo does agree with is the big-endian form compared unsigned, and
  //that is what these pairs check — against the running runtime, because the same comparison
  //once treated those three fields as signed.
  [Fact]
  public void GuidsOrderTheWayGuidCompareToOrdersThem() {
    AssertOrdersAgree(NextGuid, KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      Guid.Empty,
      new Guid("00000000-0000-0000-0000-000000000001"),
      new Guid("80000000-0000-0000-0000-000000000000"),
      new Guid("00000000-8000-0000-0000-000000000000"),
      new Guid("00000000-0000-8000-0000-000000000000"),
      new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"));
  }

  [Fact]
  public void UlidsOrderAsTheirBytes() {
    AssertOrdersAgree(NextUlid, KeyEncoder.Encode, (a, b) => a.CompareTo(b),
      Ulid.Empty, Ulid.NewUlid(), Ulid.MaxValue);
  }

  //Time-ordered, which is the reason D-1 chose them: a primary index on the record identity
  //takes every insert at the right-hand edge of the tree.
  [Fact]
  public void UlidsMintedLaterSortAfterUlidsMintedEarlier() {
    var earlier = Ulid.NewUlid(DateTimeOffset.UnixEpoch);
    var later = Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddSeconds(1));
    Assert.True(KeyComparer.Compare(KeyEncoder.Encode(earlier).Bytes,
      KeyEncoder.Encode(later).Bytes) < 0);
  }

  [Fact]
  public void StringsOrderOrdinallyOverTheirNormalisedForm() {
    AssertOrdersAgree(r => NextString(r, r.Next(0, 12)), KeyEncoder.Encode,
      (a, b) => string.CompareOrdinal(KeyNormalization.Normalize(a), KeyNormalization.Normalize(b)),
      "", "a", "A", "ab", "b", "Б", "б", "и", "й", "і", "ї", "Олександр", "олександр", "ЯЯЯ");
  }

  //The pair that decides between UTF-16 code units and UTF-8 bytes. Ordinal comparison is
  //over code units, so a surrogate pair (0xD83D...) sorts below U+E000; UTF-8 bytes would
  //sort it above, because its code point is larger. Encoding UTF-8 would put the index in an
  //order that disagrees with every ordinal comparison the engine makes elsewhere.
  [Fact]
  public void ASupplementaryCharacterSortsWhereOrdinalComparisonPutsIt() {
    const string emoji = "\U0001F600";
    const string privateUse = "\uE000";
    Assert.True(string.CompareOrdinal(emoji, privateUse) < 0);
    Assert.True(KeyComparer.Compare(
      KeyEncoder.Encode(emoji).Bytes, KeyEncoder.Encode(privateUse).Bytes) < 0);
  }

  //Folding is what makes the order the same on every machine, and it is a loss: these pairs
  //are one key, so an equality predicate over a string column has to re-check the record.
  [Fact]
  public void CaseAndDiacriticsAreFoldedIntoOneKey() {
    Assert.Equal(KeyEncoder.Encode("Олександр").Bytes, KeyEncoder.Encode("олександр").Bytes);
    Assert.Equal(KeyEncoder.Encode("Ї").Bytes, KeyEncoder.Encode("і").Bytes);
    Assert.Equal(KeyEncoder.Encode("café").Bytes, KeyEncoder.Encode("CAFE").Bytes);
    Assert.True(KeyEncoder.Encode("Олександр").IsFolded);
    Assert.True(KeyEncoder.Encode("Олександр").RequiresRecheck);
  }

  //Every other type is the whole truth about its value, so a match in the index is a match.
  [Fact]
  public void OnlyStringKeysNeedTheRecordConsultedAgain() {
    Assert.False(KeyEncoder.Encode(29).RequiresRecheck);
    Assert.False(KeyEncoder.Encode(1.5m).RequiresRecheck);
    Assert.False(KeyEncoder.Encode(DateTime.MaxValue).RequiresRecheck);
    Assert.False(KeyEncoder.Encode(Ulid.NewUlid()).RequiresRecheck);
    Assert.False(KeyEncoder.EncodeNull().RequiresRecheck);
  }

  [Fact]
  public void ALongStringIsTruncatedToAFixedMaximumAndSaysSo() {
    var shortEnough = new string('a', KeyEncoder.MaxStringKeyBytes / sizeof(char));
    Assert.False(KeyEncoder.Encode(shortEnough).IsTruncated);
    Assert.Equal(1 + KeyEncoder.MaxStringKeyBytes, KeyEncoder.Encode(shortEnough).Bytes.Length);

    var tooLong = KeyEncoder.Encode(shortEnough + "a");
    Assert.True(tooLong.IsTruncated);
    Assert.Equal(1 + KeyEncoder.MaxStringKeyBytes, tooLong.Bytes.Length);
  }

  //Truncation may turn a difference into a tie; it may never turn one order into the other.
  //Where it does tie, the key says it is truncated, which is the caller's instruction to
  //re-check the predicate against the record rather than trust the match.
  [Fact]
  public void TruncationTiesLongKeysWithoutEverInvertingThem() {
    var random = new Random(Seed);
    for (var i = 0; i < RandomPairs; i++) {
      var left = NextLongSharedPrefixString(random);
      var right = NextLongSharedPrefixString(random);
      var expected = Math.Sign(string.CompareOrdinal(
        KeyNormalization.Normalize(left), KeyNormalization.Normalize(right)));
      var leftKey = KeyEncoder.Encode(left);
      var rightKey = KeyEncoder.Encode(right);
      var actual = Math.Sign(KeyComparer.Compare(leftKey.Bytes, rightKey.Bytes));
      if (actual != 0) {
        Assert.Equal(expected, actual);
        continue;
      }
      //Equal keys for unequal strings is allowed, and only because the flag warns of it.
      Assert.True(expected == 0 || leftKey.IsTruncated && rightKey.IsTruncated,
        $"'{left}' and '{right}' share a key without being marked truncated.");
    }
  }

  [Fact]
  public void TheTypedEncodingIsWhatTheColumnTypeDispatchesTo() {
    Assert.Equal(KeyEncoder.Encode(29).Bytes, KeyEncoder.Encode(ValueTypeEnum.Int, 29).Bytes);
    Assert.Equal(KeyEncoder.Encode(1.5m).Bytes, KeyEncoder.Encode(ValueTypeEnum.Decimal, 1.5m).Bytes);
    Assert.Equal(KeyEncoder.Encode("Олександр").Bytes,
      KeyEncoder.Encode(ValueTypeEnum.String, "Олександр").Bytes);
    var identity = Ulid.NewUlid();
    Assert.Equal(KeyEncoder.Encode(identity).Bytes, KeyEncoder.Encode(ValueTypeEnum.Ulid, identity).Bytes);
  }

  //A missing value is one key whatever the column holds, so nulls collect at the left-hand
  //end of every index rather than at a place that depends on the type.
  [Theory]
  [InlineData(ValueTypeEnum.Int)]
  [InlineData(ValueTypeEnum.String)]
  [InlineData(ValueTypeEnum.DateTime)]
  [InlineData(ValueTypeEnum.Guid)]
  public void ANullValueOfAnyTypeIsTheNullKey(ValueTypeEnum type) {
    Assert.Equal(KeyEncoder.EncodeNull().Bytes, KeyEncoder.Encode(type, null).Bytes);
  }

  //An object and an array have no ordering of their own, so there is nothing to preserve.
  [Theory]
  [InlineData(ValueTypeEnum.Object)]
  [InlineData(ValueTypeEnum.Array)]
  public void AValueWithNoOrderingIsRefusedRatherThanEncodedSomehow(ValueTypeEnum type) {
    Assert.Throws<NotSupportedException>(() => KeyEncoder.Encode(type, new object()));
  }

  //Specials often enough to be hit, because random bits almost never produce one.
  private static double NextDouble(Random random) {
    return random.Next(5) switch {
      0 => double.NaN,
      1 => random.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity,
      2 => random.Next(2) == 0 ? 0.0 : -0.0,
      3 => random.Next(-1000, 1000) / 8.0,
      _ => BitConverter.Int64BitsToDouble(random.NextInt64())
    };
  }

  //Across the whole 96-bit mantissa and every scale, so the exponent and the digit run are
  //both exercised rather than only the small values a person would write by hand.
  private static decimal NextDecimal(Random random) {
    return random.Next(4) switch {
      0 => 0m,
      1 => random.Next(-1000, 1000),
      2 => new decimal(random.Next(), 0, 0, random.Next(2) == 0, (byte)random.Next(29)),
      _ => new decimal(random.Next(), random.Next(), random.Next(), random.Next(2) == 0,
        (byte)random.Next(29))
    };
  }

  //Sixteen random bytes almost always differ in the first one, which would leave every field
  //but the first untested. Most of these share a prefix, so the comparison is decided
  //somewhere in the middle where the field boundaries are.
  private static byte[] NextSharedPrefixBytes(Random random) {
    var bytes = new byte[16];
    random.NextBytes(bytes);
    var shared = random.Next(16);
    for (var i = 0; i < shared; i++) {
      bytes[i] = 0x7F;
    }
    return bytes;
  }

  private static Guid NextGuid(Random random) {
    return new Guid(NextSharedPrefixBytes(random));
  }

  private static Ulid NextUlid(Random random) {
    return new Ulid(NextSharedPrefixBytes(random));
  }

  //Ukrainian alongside Latin, because the folding rule exists for Ukrainian: и and й fold
  //together, і and ї fold together, and the ordering has to stay total anyway.
  private const string Alphabet = "aAbBzZ0 -іїИйЯяОоЛл";

  private static string NextString(Random random, int length) {
    return string.Create(length, random, (span, r) => {
      for (var i = 0; i < span.Length; i++) {
        span[i] = Alphabet[r.Next(Alphabet.Length)];
      }
    });
  }

  //Longer than the maximum and mostly identical, so the pairs actually collide after
  //truncation instead of differing in their first character.
  private static string NextLongSharedPrefixString(Random random) {
    var units = KeyEncoder.MaxStringKeyBytes / sizeof(char);
    return new string('о', random.Next(units - 2, units + 3)) + NextString(random, 8);
  }
}
