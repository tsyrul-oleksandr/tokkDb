using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;
using Xunit;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests.Delta;

//DL-8 and I-5. Arrays matched by a declared element key, the fallback to positions when the
//key cannot be trusted, and where a collection declares the key.
public class KeyedArrayDiffTests {
  private static readonly DiffOptions ByAuthorId = DiffOptions.None.WithElementKey("authors", "id");

  private static ObjectDocumentValue Author(int id, string name, int born = 1970) {
    return Obj(("id", Int(id)), ("name", Str(name)), ("born", Int(born)));
  }

  private static IReadOnlyList<DeltaElement> Diff(IDocumentValue a, IDocumentValue b, DiffOptions options,
      out DocumentDelta delta) {
    delta = DocumentDiff.Compute(a, b, options);
    Assert.True(CanonicalValue.Equal(b, delta.ApplyTo(a)), $"applying {delta} to a did not give b");
    Assert.True(CanonicalValue.Equal(a, delta.Invert().ApplyTo(b)), $"applying the inverse of {delta} to b did not give a");
    return delta.Elements;
  }

  [Fact]
  public void ReorderingThreeAuthorsWhileOneChangesGivesMovesAndOneReplace() {
    var before = Obj(("authors", Arr(Author(1, "Ann"), Author(2, "Bob"), Author(3, "Cid"))));
    var after = Obj(("authors", Arr(Author(3, "Cid"), Author(1, "Ann", born: 1971), Author(2, "Bob"))));

    var elements = Diff(before, after, ByAuthorId, out var delta);

    Assert.DoesNotContain(elements, element => element.Operation is DeltaOperation.RemoveAt or DeltaOperation.Insert);
    Assert.Contains(elements, element => element.Operation == DeltaOperation.Move);
    var replace = Assert.Single(elements, element => element.Operation == DeltaOperation.Replace);
    Assert.Equal("authors[1].born", replace.Path.Render());
    var matching = Assert.Single(delta.Matchings);
    Assert.Equal(ArrayMatchingMode.ByKey, matching.Mode);
    Assert.Equal("id", matching.Key);
    Assert.Null(matching.Reason);
    Assert.Equal(ArrayMatchingMode.ByKey, delta.MatchingFor(DeltaPath.Parse("authors")));
  }

  [Fact]
  public void AnAuthorWhoseKeyChangedIsRemovedAndInserted() {
    var before = Obj(("authors", Arr(Author(1, "Ann"), Author(2, "Bob"))));
    var after = Obj(("authors", Arr(Author(1, "Ann"), Author(9, "Bob"))));

    var elements = Diff(before, after, ByAuthorId, out _);

    Assert.Collection(elements,
      removed => {
        Assert.Equal(DeltaOperation.RemoveAt, removed.Operation);
        Assert.Equal("authors[1]", removed.Path.Render());
      },
      inserted => {
        Assert.Equal(DeltaOperation.Insert, inserted.Operation);
        Assert.Equal("authors[1]", inserted.Path.Render());
      });
  }

  [Fact]
  public void WithoutAKeyTheSameReorderingIsPositional() {
    var before = Obj(("authors", Arr(Author(1, "Ann"), Author(2, "Bob"), Author(3, "Cid"))));
    var after = Obj(("authors", Arr(Author(3, "Cid"), Author(1, "Ann", born: 1971), Author(2, "Bob"))));

    Diff(before, after, DiffOptions.None, out var delta);

    Assert.Empty(delta.Matchings);
    Assert.Equal(ArrayMatchingMode.ByPosition, delta.MatchingFor(DeltaPath.Parse("authors")));
  }

  public static IEnumerable<object[]> Untrustworthy() {
    yield return ["duplicated", Arr(Author(1, "Ann"), Author(1, "Bob")), "held by elements [0] and [1]"];
    yield return ["missing", Arr(Author(1, "Ann"), Obj(("name", Str("Bob")))), "has no 'id'"];
    yield return ["null", Arr(Author(1, "Ann"), Obj(("id", Null()), ("name", Str("Bob")))), "has a null 'id'"];
    yield return ["object", Arr(Author(1, "Ann"), Obj(("id", Obj(("x", Int(1)))), ("name", Str("Bob")))), "cannot be a key"];
    yield return ["array", Arr(Author(1, "Ann"), Obj(("id", Ints(1)), ("name", Str("Bob")))), "cannot be a key"];
    yield return ["scalar element", Arr(Author(1, "Ann"), Int(2)), "is not an object"];
  }

  [Theory]
  [MemberData(nameof(Untrustworthy))]
  public void AKeyThatCannotBeTrustedFallsBackToPositionsAndSaysSo(string @case, ArrayDocumentValue authors, string reason) {
    var before = Obj(("authors", Arr(Author(1, "Ann"), Author(2, "Bob"))));
    var after = Obj(("authors", authors));

    Diff(before, after, ByAuthorId, out var forward);
    Diff(after, before, ByAuthorId, out var backward);

    foreach (var delta in new[] { forward, backward }) {
      var matching = Assert.Single(delta.Matchings);
      Assert.Equal(ArrayMatchingMode.ByPosition, matching.Mode);
      Assert.Equal("id", matching.Key);
      Assert.Contains(reason, matching.Reason);
      Assert.Equal(ArrayMatchingMode.ByPosition, delta.MatchingFor(DeltaPath.Parse("authors")));
    }
    Assert.NotEmpty(@case);
  }

  //A key declared for "items.tags" applies to the tags of every item, whichever index the item has.
  [Fact]
  public void AKeyAppliesToTheArrayAtThePathWhateverTheIndicesAround() {
    var options = DiffOptions.None.WithElementKey(DeltaPath.Parse("items.tags"), "id");
    ObjectDocumentValue Tag(int id, string text) => Obj(("id", Int(id)), ("text", Str(text)));
    var before = Obj(("items", Arr(Obj(("tags", Arr(Tag(1, "a"), Tag(2, "b")))), Obj(("tags", Arr(Tag(1, "a"), Tag(2, "b")))))));
    var after = Obj(("items", Arr(Obj(("tags", Arr(Tag(1, "a"), Tag(2, "b")))), Obj(("tags", Arr(Tag(2, "b"), Tag(1, "a!")))))));

    var elements = Diff(before, after, options, out var delta);

    Assert.Equal(ArrayMatchingMode.ByKey, delta.MatchingFor(DeltaPath.Parse("items[1].tags")));
    Assert.Contains(elements, element => element.Operation == DeltaOperation.Move);
    var replace = Assert.Single(elements, element => element.Operation == DeltaOperation.Replace);
    Assert.Equal("items[1].tags[1].text", replace.Path.Render());
  }

  //I-5: the key is part of the column's declaration and survives the catalogue.
  [Fact]
  public void AnElementKeyIsDeclaredOnTheColumnAndSurvivesAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      db.CreateCollection("Publication", [
        new ColumnDescriptor("Title", ValueTypeEnum.String),
        new ColumnDescriptor("Authors", ValueTypeEnum.Array, elementKey: "id"),
        new ColumnDescriptor("Keywords", ValueTypeEnum.Array)
      ]);
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var columns = reopened.Collection("Publication").Columns;
    Assert.Equal("id", columns.Single(column => column.Name == "Authors").ElementKey);
    Assert.Equal("", columns.Single(column => column.Name == "Keywords").ElementKey);
    Assert.Equal("", columns.Single(column => column.Name == "Title").ElementKey);
  }

  [Fact]
  public void AnElementKeyRoundTripsThroughTheCatalogueDocument() {
    var descriptor = new CollectionDescriptor {
      Id = Ulid.NewUlid(),
      Name = "Publication",
      Columns = [new ColumnDescriptor("Authors", ValueTypeEnum.Array, elementKey: "orcid")]
    };
    var read = CollectionDescriptorDocument.Read(CollectionDescriptorDocument.Write(descriptor));
    Assert.Equal("orcid", read.Columns[0].ElementKey);
  }
}
