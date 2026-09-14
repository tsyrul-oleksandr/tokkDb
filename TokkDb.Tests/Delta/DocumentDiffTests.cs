using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using Xunit;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests.Delta;

//DL-1 and DL-5. Diffing objects and values: one element per change, fields in ordinal order,
//and equality by stored bytes.
public class DocumentDiffTests {
  [Fact]
  public void OneFieldChangedInAFiftyFieldDocumentGivesOneReplaceUnderATenthOfTheRecord() {
    var before = Record(50);
    var after = Record(50, changedField: 31, changedTo: 999);

    var delta = DocumentDiff.Compute(before, after);

    var element = Assert.Single(delta.Elements);
    Assert.Equal(DeltaOperation.Replace, element.Operation);
    Assert.Equal(DeltaPath.Parse("field31"), element.Path);
    Assert.Equal(31 * 7, Assert.IsType<IntDocumentValue>(element.OldValue.Value).Value);
    Assert.Equal(999, Assert.IsType<IntDocumentValue>(element.NewValue.Value).Value);

    var recordBytes = CanonicalValue.Bytes(after).Length;
    var deltaBytes = CanonicalValue.Bytes(delta.ToDocumentValue()).Length;
    Assert.True(deltaBytes * 10 < recordBytes, $"delta is {deltaBytes} bytes, record is {recordBytes}");
  }

  [Fact]
  public void NullAgainstAbsentGivesReplaceAgainstRemove() {
    var set = Assert.Single(DocumentDiff.Compute(Obj(("a", Int(1))), Obj(("a", Null()))).Elements);
    Assert.Equal(DeltaOperation.Replace, set.Operation);
    Assert.IsType<NullDocumentValue>(set.NewValue.Value);

    var removed = Assert.Single(DocumentDiff.Compute(Obj(("a", Int(1))), Obj()).Elements);
    Assert.Equal(DeltaOperation.Remove, removed.Operation);
    Assert.True(removed.NewValue.IsAbsent);

    var added = Assert.Single(DocumentDiff.Compute(Obj(), Obj(("a", Null()))).Elements);
    Assert.Equal(DeltaOperation.Add, added.Operation);
    Assert.True(added.OldValue.IsAbsent);
    Assert.IsType<NullDocumentValue>(added.NewValue.Value);
  }

  [Fact]
  public void ValuesCompareByStoredBytes() {
    var scale = Assert.Single(DocumentDiff.Compute(Obj(("v", Dec(1.50m))), Obj(("v", Dec(1.5m)))).Elements);
    Assert.Equal(DeltaOperation.Replace, scale.Operation);

    var width = Assert.Single(DocumentDiff.Compute(Obj(("v", Int(5))), Obj(("v", Long(5)))).Elements);
    Assert.Equal(DeltaOperation.Replace, width.Operation);

    var ticks = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc).Ticks;
    var kind = Assert.Single(DocumentDiff.Compute(
      Obj(("v", Moment(new DateTime(ticks, DateTimeKind.Utc)))),
      Obj(("v", Moment(new DateTime(ticks, DateTimeKind.Unspecified))))).Elements);
    Assert.Equal(DeltaOperation.Replace, kind.Operation);

    Assert.False(CanonicalValue.Equal(Dec(1.50m), Dec(1.5m)));
    Assert.False(CanonicalValue.Equal(Int(5), Long(5)));
    Assert.True(CanonicalValue.Equal(Dec(1.50m), Dec(1.50m)));
  }

  [Fact]
  public void EqualDocumentsGiveAnEmptyDelta() {
    var document = Obj(("a", Int(1)), ("b", Obj(("c", Str("x")), ("d", Arr(Int(1), Int(2))))));
    Assert.True(DocumentDiff.Compute(document, document).IsEmpty);
    Assert.True(DocumentDiff.Compute(document, CanonicalValue.Copy(document)).IsEmpty);

    //The same fields in another order are the same document.
    var reordered = Obj(("b", Obj(("d", Arr(Int(1), Int(2))), ("c", Str("x")))), ("a", Int(1)));
    Assert.True(DocumentDiff.Compute(document, reordered).IsEmpty);
    Assert.True(CanonicalValue.Equal(document, reordered));
  }

  [Fact]
  public void FieldsAreEmittedInOrdinalOrder() {
    var before = Obj(("b", Int(1)), ("A", Int(1)), ("a", Int(1)), ("B", Int(1)));
    var after = Obj(("b", Int(2)), ("A", Int(2)), ("a", Int(2)), ("B", Int(2)));

    var paths = DocumentDiff.Compute(before, after).Elements.Select(element => element.Path.Render());

    Assert.Equal(["A", "B", "a", "b"], paths);
  }

  [Fact]
  public void AChangeThreeObjectsDeepIsOneReplaceAtItsPath() {
    var before = Obj(("product", Obj(("manufacturer", Obj(("address", Obj(("city", Str("Kyiv")), ("zip", Str("01001")))))))));
    var after = Obj(("product", Obj(("manufacturer", Obj(("address", Obj(("city", Str("Lviv")), ("zip", Str("01001")))))))));

    var element = Assert.Single(DocumentDiff.Compute(before, after).Elements);

    Assert.Equal(DeltaOperation.Replace, element.Operation);
    Assert.Equal("product.manufacturer.address.city", element.Path.Render());
  }

  [Fact]
  public void AChangeOfTypeIsAReplaceOfTheWholeValue() {
    var toScalar = Assert.Single(DocumentDiff.Compute(Obj(("v", Obj(("x", Int(1))))), Obj(("v", Str("flat")))).Elements);
    Assert.Equal(DeltaOperation.Replace, toScalar.Operation);
    Assert.IsType<ObjectDocumentValue>(toScalar.OldValue.Value);

    var toObject = Assert.Single(DocumentDiff.Compute(Obj(("v", Arr(Int(1)))), Obj(("v", Obj(("x", Int(1)))))).Elements);
    Assert.Equal(DeltaOperation.Replace, toObject.Operation);
    Assert.IsType<ArrayDocumentValue>(toObject.OldValue.Value);
  }

  [Fact]
  public void AddedAndRemovedFieldsInsideANestedObject() {
    var before = Obj(("inner", Obj(("keep", Int(1)), ("gone", Int(2)))));
    var after = Obj(("inner", Obj(("keep", Int(1)), ("new", Int(3)))));

    var elements = DocumentDiff.Compute(before, after).Elements;

    Assert.Collection(elements,
      removed => {
        Assert.Equal(DeltaOperation.Remove, removed.Operation);
        Assert.Equal("inner.gone", removed.Path.Render());
      },
      added => {
        Assert.Equal(DeltaOperation.Add, added.Operation);
        Assert.Equal("inner.new", added.Path.Render());
      });
  }
}
