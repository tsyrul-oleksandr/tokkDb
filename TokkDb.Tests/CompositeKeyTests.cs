using TokkDb.Documents.Keys;
using TokkDb.Pages.Records;
using Xunit;

namespace TokkDb.Tests;

//D-3's composite key, and the property the whole of a secondary index rests on: ordering by
//the pair (value, recordId) has to survive being flattened into one run of bytes.
public class CompositeKeyTests {
  private const int Seed = 20260905;

  private static byte[] Key(string value, Ulid recordId) {
    return CompositeKey.Create(KeyEncoder.Encode(value), KeyEncoder.Encode(recordId));
  }

  private static byte[] Key(int value, Ulid recordId) {
    return CompositeKey.Encode(value, recordId);
  }

  //The case a plain concatenation gets wrong. "ab" is a prefix of "abc", so without a
  //terminator the identity that follows the shorter value would be compared against the
  //rest of the longer one, and which way round they came out would depend on the identity.
  [Fact]
  public void AValueThatIsAPrefixOfAnotherSortsBelowItWhateverIdentityFollows() {
    var low = Ulid.MinValue;
    var high = Ulid.MaxValue;
    foreach (var (shorter, longer) in new[] { (low, low), (low, high), (high, low), (high, high) }) {
      Assert.True(KeyComparer.Compare(Key("ab", shorter), Key("abc", longer)) < 0,
        "a value that is a prefix of another did not sort below it");
    }
  }

  //Every character below U+0100 encodes with a zero byte in it — "A" is 0x00 0x41 — so the
  //terminator has to be a pair that a value can never contain, and the value's own zeros
  //have to be escaped out of the way.
  [Fact]
  public void AValueFullOfZeroBytesStillSortsAgainstItsNeighbours() {
    var identity = Ulid.NewUlid();
    Assert.True(KeyComparer.Compare(Key("A", identity), Key("AA", identity)) < 0);
    Assert.True(KeyComparer.Compare(Key("AA", identity), Key("AB", identity)) < 0);
    Assert.True(KeyComparer.Compare(Key("", identity), Key("A", identity)) < 0);
  }

  //The identity only decides between records that carry the same value; a difference in the
  //value settles it whatever the identities are.
  [Fact]
  public void TheValueDecidesFirstAndTheIdentityOnlyBreaksTies() {
    //Minted monotonically, because two Ulid.NewUlid() calls inside one millisecond are not
    //ordered against each other — the finding that put RecordIdentity there in the first place.
    var first = RecordIdentity.Next();
    var second = RecordIdentity.Next();
    Assert.True(first.CompareTo(second) < 0);

    Assert.True(KeyComparer.Compare(Key("a", second), Key("b", first)) < 0);
    Assert.True(KeyComparer.Compare(Key("a", first), Key("a", second)) < 0);
    Assert.Equal(0, KeyComparer.Compare(Key("a", first), Key("a", first)));
  }

  //Random pairs, because the two interesting cases are easy to name and easy to be lucky about.
  [Fact]
  public void OrderingByBytesAgreesWithOrderingByValueThenIdentity() {
    var random = new Random(Seed);
    const string alphabet = "abЇїИ ";
    for (var i = 0; i < 5_000; i++) {
      var leftValue = NextString(random, alphabet);
      var rightValue = NextString(random, alphabet);
      var leftId = Ulid.NewUlid();
      var rightId = Ulid.NewUlid();

      var expected = string.CompareOrdinal(KeyNormalization.Normalize(leftValue),
        KeyNormalization.Normalize(rightValue));
      expected = expected != 0 ? Math.Sign(expected) : Math.Sign(leftId.CompareTo(rightId));
      var actual = Math.Sign(KeyComparer.Compare(Key(leftValue, leftId), Key(rightValue, rightId)));
      Assert.True(expected == actual,
        $"('{leftValue}', {leftId}) against ('{rightValue}', {rightId}): expected {expected}, got {actual}");
    }
  }

  [Fact]
  public void IntegerValuesOrderInTheCompositeAsTheyDoAlone() {
    var random = new Random(Seed);
    for (var i = 0; i < 5_000; i++) {
      var left = random.Next(-1000, 1000);
      var right = random.Next(-1000, 1000);
      var leftId = Ulid.NewUlid();
      var rightId = Ulid.NewUlid();
      var expected = left != right ? Math.Sign(left.CompareTo(right)) : Math.Sign(leftId.CompareTo(rightId));
      Assert.Equal(expected, Math.Sign(KeyComparer.Compare(Key(left, leftId), Key(right, rightId))));
    }
  }

  //The bounds a lookup by value uses: every composite key for that value falls inside them,
  //and no key for any other value does.
  [Fact]
  public void TheBoundsOfOneValueHoldExactlyTheKeysBuiltFromIt() {
    var value = KeyEncoder.Encode("ab");
    var from = CompositeKey.ValuePrefix(value);
    var to = CompositeKey.AboveValuePrefix(value);

    foreach (var identity in new[] { Ulid.MinValue, Ulid.NewUlid(), Ulid.MaxValue }) {
      var key = Key("ab", identity);
      Assert.True(KeyComparer.Compare(from, key) <= 0, "a key of the value fell below the lower bound");
      Assert.True(KeyComparer.Compare(key, to) < 0, "a key of the value reached the upper bound");
    }
    foreach (var other in new[] { "a", "abc", "aa", "b", "" }) {
      var key = Key(other, Ulid.NewUlid());
      Assert.True(KeyComparer.Compare(key, from) < 0 || KeyComparer.Compare(key, to) >= 0,
        $"'{other}' fell inside the bounds of 'ab'");
    }
  }

  [Fact]
  public void TheIdentityIsReadBackOutOfTheCompositeKey() {
    var identity = Ulid.NewUlid();
    Assert.Equal(identity, CompositeKey.ReadRecordId(Key("Олександр", identity)));
    Assert.Equal(identity, CompositeKey.ReadRecordId(Key(-29, identity)));
    Assert.Equal(identity, CompositeKey.ReadRecordId(
      CompositeKey.Create(KeyEncoder.EncodeNull(), KeyEncoder.Encode(identity))));
  }

  private static string NextString(Random random, string alphabet) {
    var length = random.Next(0, 5);
    return string.Create(length, random, (span, r) => {
      for (var i = 0; i < span.Length; i++) {
        span[i] = alphabet[r.Next(alphabet.Length)];
      }
    });
  }
}
