using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using Xunit;

namespace TokkDb.Tests.Delta;

//DL-2. Paths are segments; the rendering is for people and has to be unambiguous.
public class DeltaPathTests {
  public static IEnumerable<object[]> Rendered() {
    yield return [DeltaPath.Root.Field("a").Field("b").At(3).Field("c"), "a.b[3].c"];
    yield return [DeltaPath.Root.Field("a.b").At(0), "\"a.b\"[0]"];
    yield return [DeltaPath.Root.Field("x[0]"), "\"x[0]\""];
    yield return [DeltaPath.Root.Field("say \"hi\"").Field("q"), "\"say \\\"hi\\\"\".q"];
    yield return [DeltaPath.Root.Field("back\\slash"), "\"back\\\\slash\""];
    yield return [DeltaPath.Root.Field(""), "\"\""];
    yield return [DeltaPath.Root.At(2).Field("c"), "[2].c"];
    yield return [DeltaPath.Root, ""];
  }

  [Theory]
  [MemberData(nameof(Rendered))]
  public void RendersAsExpected(DeltaPath path, string rendered) {
    Assert.Equal(rendered, path.Render());
  }

  [Theory]
  [MemberData(nameof(Rendered))]
  public void ParsingTheRenderingGivesTheSamePath(DeltaPath path, string rendered) {
    Assert.Equal(path, DeltaPath.Parse(rendered));
  }

  //The names that would be misread without quoting: a dot inside a name is not a second
  //segment, and a bracket inside a name is not an index.
  [Fact]
  public void AFieldNamedLikeAPathIsNotThatPath() {
    var dotted = DeltaPath.Root.Field("a.b");
    var nested = DeltaPath.Root.Field("a").Field("b");
    Assert.NotEqual(dotted, nested);
    Assert.NotEqual(dotted.Render(), nested.Render());
    Assert.Equal(dotted, DeltaPath.Parse(dotted.Render()));
    Assert.Equal(nested, DeltaPath.Parse(nested.Render()));

    var bracketed = DeltaPath.Root.Field("x[0]");
    var indexed = DeltaPath.Root.Field("x").At(0);
    Assert.NotEqual(bracketed, indexed);
    Assert.NotEqual(bracketed.Render(), indexed.Render());
    Assert.Equal(bracketed, DeltaPath.Parse(bracketed.Render()));
    Assert.Equal(indexed, DeltaPath.Parse(indexed.Render()));
  }

  [Fact]
  public void ADottedPathAddressesAValueThreeObjectsDeep() {
    var path = DeltaPath.Parse("product.manufacturer.address.city");
    Assert.Equal(4, path.Length);
    Assert.All(path.Segments, segment => Assert.True(segment.IsField));
    Assert.Equal(["product", "manufacturer", "address", "city"], path.Segments.Select(segment => segment.Name));
  }

  //Storage keeps the segments: a string for a field and an int for an index, never the rendering.
  [Theory]
  [MemberData(nameof(Rendered))]
  public void RoundTripsThroughStorageAsSegments(DeltaPath path, string rendered) {
    var stored = Assert.IsType<ArrayDocumentValue>(path.ToDocumentValue());
    Assert.Equal(path.Length, stored.Values.Length);
    for (var i = 0; i < path.Length; i++) {
      if (path.Segments[i].IsIndex) {
        Assert.Equal(path.Segments[i].Index, Assert.IsType<IntDocumentValue>(stored.Values[i]).Value);
      } else {
        Assert.Equal(path.Segments[i].Name, Assert.IsType<StringDocumentValue>(stored.Values[i]).Value);
      }
    }
    Assert.Equal(path, DeltaPath.FromDocumentValue(stored));
    Assert.Equal(rendered, DeltaPath.FromDocumentValue(stored).Render());
  }

  [Theory]
  [InlineData(".a")]
  [InlineData("a..b")]
  [InlineData("a.")]
  [InlineData("a[")]
  [InlineData("a[x]")]
  [InlineData("a]")]
  [InlineData("\"a")]
  [InlineData("a\"b")]
  [InlineData("a b.c[1]d")]
  public void RefusesWhatItCannotReadBack(string text) {
    Assert.Throws<FormatException>(() => DeltaPath.Parse(text));
  }

  [Fact]
  public void ParentAndLastAndWithoutIndices() {
    var path = DeltaPath.Parse("authors[2].emails[0]");
    Assert.Equal(DeltaPath.Parse("authors[2].emails"), path.Parent);
    Assert.True(path.Last.IsIndex);
    Assert.Equal(0, path.Last.Index);
    Assert.Equal(DeltaPath.Parse("authors.emails"), path.WithoutIndices());
    Assert.Throws<InvalidOperationException>(() => DeltaPath.Root.Parent);
  }
}
