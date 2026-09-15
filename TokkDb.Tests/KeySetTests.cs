using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages.Query;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//RL-6 and Q-9: the purpose-built set of compact encoded keys, and the cap over the bytes it has
//actually allocated.
public class KeySetTests {
  private readonly ITestOutputHelper _output;

  public KeySetTests(ITestOutputHelper output) {
    _output = output;
  }

  private static byte[] Key(int value) => KeyEncoder.Encode(value).Bytes;

  [Fact]
  public void TheSameKeyTwiceIsOneMember() {
    var set = new KeySet("R", long.MaxValue);

    Assert.True(set.Add(Key(1)));
    Assert.True(set.Add(Key(2)));
    Assert.False(set.Add(Key(1)));

    Assert.Equal(2, set.Count);
    Assert.True(set.Contains(Key(1)));
    Assert.True(set.Contains(Key(2)));
    Assert.False(set.Contains(Key(3)));
    Assert.Equal(2 * Key(1).Length, set.KeyBytes);
  }

  [Fact]
  public void TheKeysComeOutInKeyOrderAndAsValuesThatEncodeToThemselves() {
    var set = new KeySet("R", long.MaxValue);
    foreach (var value in new[] { 30, -5, 7, int.MaxValue, 0 }) {
      set.Add(Key(value));
    }

    var sorted = set.SortedKeys().ToList();
    Assert.Equal(new[] { -5, 0, 7, 30, int.MaxValue }.Select(Key), sorted);
    Assert.Equal(5, set.Count);
    foreach (var value in set) {
      var key = Assert.IsType<EncodedKeyValue>(value);
      Assert.True(set.Contains(KeyEncoder.Encode(key).Bytes));
      Assert.Equal(TokkDb.Values.ValueTypeEnum.Long, key.Type);
    }
    //A string key stays folded, so a seek by it is still re-checked (D-3).
    Assert.True(new EncodedKeyValue(KeyEncoder.Encode("Lviv").Bytes).Key.RequiresRecheck);
    Assert.False(new EncodedKeyValue(Key(1)).Key.RequiresRecheck);
  }

  //Thousands of nine-byte keys: what the set allocated is more than the sum of the keys, and the
  //cap is compared against the allocation.
  [Fact]
  public void TheCapIsComparedAgainstTheSetsOwnAllocationAndNotTheSumOfItsKeys() {
    var set = new KeySet("ExpenseConference", 16 * 1024);
    var added = 0;

    var failed = Assert.Throws<QueryCapExceededException>(() => {
      for (var i = 0; i < 100_000; i++) {
        set.Add(Key(i));
        added++;
      }
    });

    _output.WriteLine($"{added} keys, {set.KeyBytes} key bytes, {set.AllocatedBytes} allocated: {failed.Message}");
    Assert.Equal(QueryCap.KeySet, failed.Cap);
    Assert.Equal("ExpenseConference", failed.Subject);
    Assert.Equal(16 * 1024, failed.Limit);
    Assert.Equal(set.AllocatedBytes, failed.SizeReached);
    Assert.True(failed.SizeReached > failed.Limit);
    Assert.True(set.AllocatedBytes > set.KeyBytes, "the allocation is the larger figure on short keys");
    Assert.True(set.AllocatedBytes < 4 * set.KeyBytes + 1024, "and not wildly larger: the set is compact");
    Assert.True(added > 300, $"only {added} keys fit under a 16 KiB cap");
  }

  [Fact]
  public void NothingIsAllocatedFromACountAndTheEmptySetAllocatesNothing() {
    var set = new KeySet("R", long.MaxValue);
    Assert.Equal(0, set.AllocatedBytes);
    set.Add(Key(1));
    Assert.True(set.AllocatedBytes < 1024, $"{set.AllocatedBytes} bytes for one key");
  }

  [Fact]
  public void ADeferredSetHoldsNothingAnswersNothingAndRefusesAKey() {
    var deferred = KeySet.Deferred("R");

    Assert.False(deferred.IsProjected);
    Assert.Equal(0, deferred.Count);
    Assert.False(deferred.Contains(Key(1)));
    Assert.Throws<InvalidOperationException>(() => deferred.Add(Key(1)));
    Assert.Equal("the keys projected across R", deferred.Describe());
    Assert.Equal("0 keys projected across R", new KeySet("R", 1024).Describe());
  }

  [Fact]
  public void GrowthKeepsEveryKeyFindable() {
    var set = new KeySet("R", long.MaxValue);
    var random = new Random(3);
    var keys = Enumerable.Range(0, 5_000).Select(_ => KeyEncoder.Encode(random.Next()).Bytes).ToList();
    foreach (var key in keys) {
      set.Add(key);
    }

    Assert.Equal(keys.Select(Convert.ToHexString).Distinct().Count(), set.Count);
    Assert.All(keys, key => Assert.True(set.Contains(key)));
    Assert.False(set.Contains(KeyEncoder.Encode(-1).Bytes));
    Assert.Equal(set.Count, set.SortedKeys().Count());
  }
}
