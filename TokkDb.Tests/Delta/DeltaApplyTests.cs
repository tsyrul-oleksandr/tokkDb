using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using Xunit;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests.Delta;

//DL-4 and DL-7. Applying checks every old value and either gives the whole result or nothing;
//inverting turns a delta into the one that takes the result back.
public class DeltaApplyTests {
  private static ObjectDocumentValue Before() {
    return Obj(
      ("x", Obj(("y", Int(1)), ("z", Str("keep")))),
      ("list", Ints(1, 2, 3)),
      ("gone", Bool(true)));
  }

  private static ObjectDocumentValue After() {
    return Obj(
      ("x", Obj(("y", Int(2)), ("z", Str("keep")), ("w", Null()))),
      ("list", Ints(3, 1, 9)),
      ("added", Dec(1.5m)));
  }

  [Fact]
  public void ApplyingGivesTheOtherDocumentAndLeavesTheInputAlone() {
    var before = Before();
    var after = After();
    var delta = DocumentDiff.Compute(before, after);

    var applied = delta.ApplyTo(before);

    Assert.True(CanonicalValue.Equal(after, applied));
    Assert.True(CanonicalValue.Equal(Before(), before), "the input was changed");
    Assert.NotSame(before, applied);
  }

  [Fact]
  public void ApplyingTheInverseGivesTheOriginal() {
    var before = Before();
    var after = After();
    var delta = DocumentDiff.Compute(before, after);

    Assert.True(CanonicalValue.Equal(before, delta.Invert().ApplyTo(after)));
    Assert.True(CanonicalValue.Equal(after, delta.Invert().Invert().ApplyTo(before)));
  }

  [Fact]
  public void ACorruptedOldValueThrowsNamingThePathAndTheVersion() {
    var before = Before();
    var delta = DocumentDiff.Compute(before, After());
    var corrupted = (ObjectDocumentValue)CanonicalValue.Copy(before);
    ((ObjectDocumentValue)corrupted.Values["x"]).Values["y"] = Int(100);
    var version = Ulid.NewUlid();

    var mismatch = Assert.Throws<DeltaMismatchException>(() => delta.ApplyTo(corrupted, version));

    Assert.Equal("x.y", mismatch.Path.Render());
    Assert.Equal(version, mismatch.Version);
    Assert.Contains(version.ToString(), mismatch.Message);
    Assert.Contains("x.y", mismatch.Message);
    Assert.Contains("Int 100", mismatch.Message);
  }

  [Fact]
  public void AMismatchLeavesNothingPartiallyApplied() {
    var delta = new DocumentDelta([
      DeltaElement.Replace(DeltaPath.Parse("a"), Int(1), Int(2)),
      DeltaElement.Replace(DeltaPath.Parse("b"), Int(1), Int(2))
    ]);
    var document = Obj(("a", Int(1)), ("b", Int(5)));

    var mismatch = Assert.Throws<DeltaMismatchException>(() => delta.ApplyTo(document));

    Assert.Equal("b", mismatch.Path.Render());
    Assert.Null(mismatch.Version);
    //The first element would have applied; the document shows no sign of it.
    Assert.Equal(1, Assert.IsType<IntDocumentValue>(document.Values["a"]).Value);
  }

  [Fact]
  public void AWrongShapeIsAMismatchToo() {
    var missingField = new DocumentDelta([DeltaElement.Replace(DeltaPath.Parse("a.b"), Int(1), Int(2))]);
    Assert.Throws<DeltaMismatchException>(() => missingField.ApplyTo(Obj(("a", Int(1)))));

    var shortArray = new DocumentDelta([DeltaElement.RemoveAt(DeltaPath.Parse("a[3]"), Int(1))]);
    Assert.Throws<DeltaMismatchException>(() => shortArray.ApplyTo(Obj(("a", Ints(1)))));

    var addOverExisting = new DocumentDelta([DeltaElement.Add(DeltaPath.Parse("a"), Int(1))]);
    Assert.Throws<DeltaMismatchException>(() => addOverExisting.ApplyTo(Obj(("a", Int(1)))));
  }

  [Fact]
  public void InvertSwapsEachOperationWithItsOpposite() {
    var list = DeltaPath.Parse("list");
    var delta = new DocumentDelta([
      DeltaElement.Add(DeltaPath.Parse("a"), Int(1)),
      DeltaElement.Remove(DeltaPath.Parse("b"), Int(2)),
      DeltaElement.Replace(DeltaPath.Parse("c"), Int(3), Int(4)),
      DeltaElement.Insert(list.At(0), Int(5)),
      DeltaElement.RemoveAt(list.At(1), Int(6)),
      DeltaElement.Move(list.At(2), Int(7), 0)
    ]);

    var inverted = delta.Invert().Elements;

    Assert.Equal(delta.Elements.Count, inverted.Count);
    Assert.Collection(inverted,
      move => {
        Assert.Equal(DeltaOperation.Move, move.Operation);
        Assert.Equal("list[0]", move.Path.Render());
        Assert.Equal(2, move.MoveTo);
      },
      insert => {
        Assert.Equal(DeltaOperation.Insert, insert.Operation);
        Assert.Equal("list[1]", insert.Path.Render());
        Assert.Equal(6, Assert.IsType<IntDocumentValue>(insert.NewValue.Value).Value);
      },
      removeAt => {
        Assert.Equal(DeltaOperation.RemoveAt, removeAt.Operation);
        Assert.Equal(5, Assert.IsType<IntDocumentValue>(removeAt.OldValue.Value).Value);
      },
      replace => {
        Assert.Equal(DeltaOperation.Replace, replace.Operation);
        Assert.Equal(4, Assert.IsType<IntDocumentValue>(replace.OldValue.Value).Value);
        Assert.Equal(3, Assert.IsType<IntDocumentValue>(replace.NewValue.Value).Value);
      },
      add => {
        Assert.Equal(DeltaOperation.Add, add.Operation);
        Assert.Equal("b", add.Path.Render());
      },
      remove => {
        Assert.Equal(DeltaOperation.Remove, remove.Operation);
        Assert.Equal("a", remove.Path.Render());
      });
  }

  [Fact]
  public void AMoveAndItsInverseAgreeAboutEveryIntermediateArray() {
    var before = Obj(("a", Ints(1, 2, 3, 4, 5)));
    foreach (var after in new[] { Ints(5, 1, 2, 3, 4), Ints(2, 3, 4, 5, 1), Ints(1, 3, 2, 5, 4), Ints(4, 2, 3, 1, 5) }) {
      var target = Obj(("a", after));
      var delta = DocumentDiff.Compute(before, target);
      Assert.True(CanonicalValue.Equal(target, delta.ApplyTo(before)), delta.ToString());
      Assert.True(CanonicalValue.Equal(before, delta.Invert().ApplyTo(target)), delta.Invert().ToString());
    }
  }

  [Fact]
  public void TheRootItselfCanBeReplaced() {
    var delta = DocumentDiff.Compute(Int(1), Str("one"));
    var element = Assert.Single(delta.Elements);
    Assert.True(element.Path.IsRoot);
    Assert.True(CanonicalValue.Equal(Str("one"), delta.ApplyTo(Int(1))));
    Assert.True(CanonicalValue.Equal(Int(1), delta.Invert().ApplyTo(Str("one"))));
  }
}
