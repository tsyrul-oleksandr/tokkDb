using TokkDb.Documents.Delta;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests.Delta;

//DL-4's acceptance criterion: over generated pairs, Compute(a, b).ApplyTo(a) gives b and the
//inverse gives a back, canonically, in positional and in keyed mode. A failure prints its seed.
public class DeltaPropertyTests(ITestOutputHelper output) {
  private const int Pairs = 10_000;
  private const int MasterSeed = 20260913;

  [Fact]
  public void ComputeApplyAndInvertHoldInPositionalMode() {
    Run(keyed: false);
  }

  [Fact]
  public void ComputeApplyAndInvertHoldInKeyedMode() {
    Run(keyed: true);
  }

  private void Run(bool keyed) {
    var options = keyed
      ? DiffOptions.None.WithElementKey("items", "id").WithElementKey(DeltaPath.Parse("a.items"), "id")
      : DiffOptions.None;
    var elements = 0L;
    var keyedArrays = 0;
    var fallbacks = 0;
    for (var pair = 0; pair < Pairs; pair++) {
      var seed = HashCode.Combine(MasterSeed, pair, keyed);
      try {
        var generator = new DocumentGenerator(new Random(seed), keyed);
        var a = generator.Document();
        //Mostly edits of a; sometimes an unrelated document, so whole subtrees are replaced.
        var b = pair % 10 == 9 ? generator.Document() : generator.Mutate(a);

        var delta = DocumentDiff.Compute(a, b, options);
        elements += delta.Elements.Count;
        keyedArrays += delta.Matchings.Count(matching => matching.Mode == ArrayMatchingMode.ByKey);
        fallbacks += delta.Matchings.Count(matching => matching.Mode == ArrayMatchingMode.ByPosition);

        Assert.True(CanonicalValue.Equal(b, delta.ApplyTo(a)), "Compute(a, b).ApplyTo(a) is not b");
        Assert.True(CanonicalValue.Equal(a, delta.Invert().ApplyTo(b)), "Compute(a, b).Invert().ApplyTo(b) is not a");
        //And the same after a trip through storage (DL-6).
        var stored = DocumentDelta.FromDocumentValue(delta.ToDocumentValue());
        Assert.True(CanonicalValue.Equal(b, stored.ApplyTo(a)), "the stored delta does not apply");
        Assert.True(CanonicalValue.Equal(a, stored.Invert().ApplyTo(b)), "the stored delta does not invert");
        if (keyed && delta.Matchings.Count > 0) {
          Assert.All(delta.Matchings, matching => Assert.Equal("id", matching.Key));
        }
      } catch (Exception exception) {
        output.WriteLine($"pair {pair} failed with seed {seed}: {exception.Message}");
        throw new Xunit.Sdk.XunitException($"pair {pair} failed with seed {seed}: {exception.Message}", exception);
      }
    }
    output.WriteLine($"{Pairs:N0} pairs, {elements:N0} elements, {keyedArrays:N0} arrays matched by key, {fallbacks:N0} fell back to positions");
    if (keyed) {
      Assert.True(keyedArrays > 0, "no array was matched by key");
      Assert.True(fallbacks > 0, "no array fell back to positions");
    }
  }
}
