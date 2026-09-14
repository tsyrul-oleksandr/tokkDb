using System.Diagnostics;
using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests.Delta;

//DL-3. Arrays by position: aligned on their bytes, gaps of equal length compared inside,
//unequal gaps removed and inserted, and an element that went from one place to another moved.
public class ArrayDiffTests(ITestOutputHelper output) {
  private static IReadOnlyList<DeltaElement> Diff(IDocumentValue a, IDocumentValue b) {
    var delta = DocumentDiff.Compute(a, b);
    //Whatever the elements are, applying them in list order has to give b (DL-4).
    Assert.True(CanonicalValue.Equal(b, delta.ApplyTo(a)), $"applying {delta} to a did not give b");
    return delta.Elements;
  }

  [Fact]
  public void AMiddleInsertionGivesOneInsert() {
    var element = Assert.Single(Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(1, 2, 9, 3, 4)))));
    Assert.Equal(DeltaOperation.Insert, element.Operation);
    Assert.Equal("a[2]", element.Path.Render());
    Assert.Equal(9, Assert.IsType<IntDocumentValue>(element.NewValue.Value).Value);
  }

  [Fact]
  public void ARemovalGivesOneRemoveAt() {
    var element = Assert.Single(Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(1, 3, 4)))));
    Assert.Equal(DeltaOperation.RemoveAt, element.Operation);
    Assert.Equal("a[1]", element.Path.Render());
    Assert.Equal(2, Assert.IsType<IntDocumentValue>(element.OldValue.Value).Value);
  }

  [Fact]
  public void ASwapGivesAMoveAndNoInsertOrRemove() {
    var element = Assert.Single(Diff(Obj(("a", Ints(1, 2))), Obj(("a", Ints(2, 1)))));
    Assert.Equal(DeltaOperation.Move, element.Operation);

    var rotated = Assert.Single(Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(2, 3, 4, 1)))));
    Assert.Equal(DeltaOperation.Move, rotated.Operation);
    Assert.Equal("a[0]", rotated.Path.Render());
    Assert.Equal(3, rotated.MoveTo);

    //Two elements that are not neighbours cannot change places in one Move; they take two
    //elements, and still no insert or remove.
    var swappedInside = Diff(Obj(("a", Ints(1, 2, 3, 4, 5))), Obj(("a", Ints(1, 4, 3, 2, 5))));
    Assert.Equal(2, swappedInside.Count);
    Assert.DoesNotContain(swappedInside, element => element.Operation is DeltaOperation.Insert or DeltaOperation.RemoveAt);
  }

  [Fact]
  public void OneFieldChangedInTheThirdObjectGivesOneReplaceAtItsField() {
    ObjectDocumentValue Author(string name, int born) => Obj(("name", Str(name)), ("born", Int(born)));
    var before = Obj(("authors", Arr(Author("A", 1970), Author("B", 1980), Author("C", 1990))));
    var after = Obj(("authors", Arr(Author("A", 1970), Author("B", 1980), Author("C", 1991))));

    var element = Assert.Single(Diff(before, after));

    Assert.Equal(DeltaOperation.Replace, element.Operation);
    Assert.Equal("authors[2].born", element.Path.Render());
  }

  [Fact]
  public void AnEqualGapIsComparedElementByElement() {
    var elements = Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(1, 7, 8, 4))));
    Assert.Collection(elements,
      first => {
        Assert.Equal(DeltaOperation.Replace, first.Operation);
        Assert.Equal("a[1]", first.Path.Render());
      },
      second => {
        Assert.Equal(DeltaOperation.Replace, second.Operation);
        Assert.Equal("a[2]", second.Path.Render());
      });
  }

  [Fact]
  public void AnUnequalGapBecomesRemoveAtAndInsert() {
    var elements = Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(1, 7, 4))));
    Assert.Collection(elements,
      first => {
        Assert.Equal(DeltaOperation.RemoveAt, first.Operation);
        Assert.Equal("a[1]", first.Path.Render());
        Assert.Equal(2, Assert.IsType<IntDocumentValue>(first.OldValue.Value).Value);
      },
      second => {
        //The second removal is at the same index: the first one has shifted it down.
        Assert.Equal(DeltaOperation.RemoveAt, second.Operation);
        Assert.Equal("a[1]", second.Path.Render());
        Assert.Equal(3, Assert.IsType<IntDocumentValue>(second.OldValue.Value).Value);
      },
      third => {
        Assert.Equal(DeltaOperation.Insert, third.Operation);
        Assert.Equal("a[1]", third.Path.Render());
      });
  }

  [Fact]
  public void AnElementThatMovedAndAnotherThatChangedAreTwoElements() {
    var elements = Diff(Obj(("a", Ints(1, 2, 3, 4))), Obj(("a", Ints(4, 1, 2, 9))));
    Assert.Contains(elements, element => element.Operation == DeltaOperation.Move);
    Assert.DoesNotContain(elements, element => element.Operation == DeltaOperation.Insert
      && Assert.IsType<IntDocumentValue>(element.NewValue.Value).Value == 4);
  }

  [Fact]
  public void ArraysWithNothingInCommonAreRemovedAndInserted() {
    var elements = Diff(Obj(("a", Ints(1, 2, 3))), Obj(("a", Ints(4, 5, 6, 7))));
    Assert.Equal(3, elements.Count(element => element.Operation == DeltaOperation.RemoveAt));
    Assert.Equal(4, elements.Count(element => element.Operation == DeltaOperation.Insert));
  }

  [Fact]
  public void NestedArraysAreDiffedInside() {
    var before = Obj(("m", Arr(Ints(1, 2), Ints(3, 4))));
    var after = Obj(("m", Arr(Ints(1, 2), Ints(3, 5, 4))));
    var element = Assert.Single(Diff(before, after));
    Assert.Equal(DeltaOperation.Insert, element.Operation);
    Assert.Equal("m[1][1]", element.Path.Render());
  }

  [Fact]
  public void AHundredThousandScalarsWithOneInsertionDiffInMilliseconds() {
    const int count = 100_000;
    var before = new IDocumentValue[count];
    var after = new IDocumentValue[count + 1];
    for (var i = 0; i < count; i++) {
      before[i] = Int(i);
      after[i < count / 2 ? i : i + 1] = Int(i);
    }
    after[count / 2] = Int(-1);
    var a = Obj(("a", Arr(before)));
    var b = Obj(("a", Arr(after)));

    DocumentDiff.Compute(a, b);
    var watch = Stopwatch.StartNew();
    var delta = DocumentDiff.Compute(a, b);
    watch.Stop();

    output.WriteLine($"{count:N0} scalars with one insertion: {watch.Elapsed.TotalMilliseconds:F1} ms");
    var element = Assert.Single(delta.Elements);
    Assert.Equal(DeltaOperation.Insert, element.Operation);
    Assert.Equal($"a[{count / 2}]", element.Path.Render());
    Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
  }

  //Two arrays that share nothing but are long: the linear-space search has to finish in
  //bounded memory and reasonable time even when the edit distance is the whole length. With
  //the same length on both sides the one gap is equal and every element is compared inside,
  //so the result is one Replace per position; one element longer, and it is removes and inserts.
  [Fact]
  public void LongUnrelatedArraysDiffWithoutQuadraticMemory() {
    const int count = 5_000;
    var a = Obj(("a", Arr(Enumerable.Range(0, count).Select(Int).ToArray())));
    var b = Obj(("a", Arr(Enumerable.Range(count, count).Select(Int).ToArray())));
    var longer = Obj(("a", Arr(Enumerable.Range(count, count + 1).Select(Int).ToArray())));

    //This thread's allocations only: other test classes run beside this one.
    var before = GC.GetAllocatedBytesForCurrentThread();
    var delta = DocumentDiff.Compute(a, b);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    var unequal = DocumentDiff.Compute(a, longer);

    output.WriteLine($"{count:N0} against {count:N0} unrelated: {allocated / 1024:N0} KB allocated");
    Assert.Equal(count, delta.Elements.Count);
    Assert.All(delta.Elements, element => Assert.Equal(DeltaOperation.Replace, element.Operation));
    Assert.Equal(count, unequal.Elements.Count(element => element.Operation == DeltaOperation.RemoveAt));
    Assert.Equal(count + 1, unequal.Elements.Count(element => element.Operation == DeltaOperation.Insert));
    Assert.True(CanonicalValue.Equal(longer, unequal.ApplyTo(a)));
    //Quadratic would be 25 million entries of four bytes; linear is a few MB, most of it the
    //result itself.
    Assert.True(allocated < 20 * 1024 * 1024, $"allocated {allocated:N0} bytes");
  }

  //The alignment is a longest common subsequence: as long as the textbook table gives, and
  //made of equal elements in increasing order on both sides.
  [Fact]
  public void TheAlignmentIsALongestCommonSubsequence() {
    var random = new Random(20260913);
    for (var round = 0; round < 500; round++) {
      var n = random.Next(0, 25);
      var m = random.Next(0, 25);
      var left = Enumerable.Range(0, n).Select(_ => random.Next(4)).ToArray();
      var right = Enumerable.Range(0, m).Select(_ => random.Next(4)).ToArray();

      var pairs = SequenceAlignment.LongestCommonSubsequence(n, m, (i, j) => left[i] == right[j]);

      Assert.Equal(TableLcs(left, right), pairs.Count);
      var previous = (-1, -1);
      foreach (var (i, j) in pairs) {
        Assert.True(i > previous.Item1 && j > previous.Item2, $"pairs are not increasing in round {round}");
        Assert.Equal(left[i], right[j]);
        previous = (i, j);
      }
    }
  }

  private static int TableLcs(int[] left, int[] right) {
    var table = new int[left.Length + 1, right.Length + 1];
    for (var i = 1; i <= left.Length; i++) {
      for (var j = 1; j <= right.Length; j++) {
        table[i, j] = left[i - 1] == right[j - 1]
          ? table[i - 1, j - 1] + 1
          : Math.Max(table[i - 1, j], table[i, j - 1]);
      }
    }
    return table[left.Length, right.Length];
  }
}
