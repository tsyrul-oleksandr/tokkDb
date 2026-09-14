using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;
using Xunit;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests.Delta;

//DL-1 and DL-6. A delta is stored as an ordinary document value: absent stays absent, null
//stays null, and a large one goes through the same record path as any large document.
public class DeltaStorageTests {
  private static DocumentDelta RoundTrip(DocumentDelta delta) {
    var slice = new BufferSlice(new byte[64 * 1024]);
    new BufferWriter(slice).Write(delta.ToDocumentValue());
    return DocumentDelta.FromDocumentValue(new BufferReader(slice).Read());
  }

  [Fact]
  public void AbsentAndNullSurviveARoundTripAsDifferentThings() {
    var delta = new DocumentDelta([
      DeltaElement.Remove(DeltaPath.Parse("gone"), Int(1)),
      DeltaElement.Replace(DeltaPath.Parse("cleared"), Int(2), Null()),
      DeltaElement.Add(DeltaPath.Parse("added"), Null())
    ]);

    var read = RoundTrip(delta);

    var removed = read.Elements[0];
    Assert.Equal(DeltaOperation.Remove, removed.Operation);
    Assert.True(removed.OldValue.IsPresent);
    Assert.True(removed.NewValue.IsAbsent);

    var cleared = read.Elements[1];
    Assert.Equal(DeltaOperation.Replace, cleared.Operation);
    Assert.True(cleared.NewValue.IsPresent);
    Assert.IsType<NullDocumentValue>(cleared.NewValue.Value);

    var added = read.Elements[2];
    Assert.True(added.OldValue.IsAbsent);
    Assert.IsType<NullDocumentValue>(added.NewValue.Value);
    Assert.NotEqual(added.OldValue, added.NewValue);
  }

  [Fact]
  public void EveryOperationAndMatchingRoundTrips() {
    var authors = DeltaPath.Parse("authors");
    var delta = new DocumentDelta([
        DeltaElement.Add(DeltaPath.Parse("a"), Str("new")),
        DeltaElement.Remove(DeltaPath.Parse("b.c"), Int(3)),
        DeltaElement.Replace(DeltaPath.Parse("\"d.e\"[1]"), Dec(1.50m), Dec(1.5m)),
        DeltaElement.Insert(authors.At(0), Obj(("id", Int(7)))),
        DeltaElement.RemoveAt(authors.At(2), Obj(("id", Int(8)))),
        DeltaElement.Move(authors.At(3), Obj(("id", Int(9))), 1),
        DeltaElement.Replace(DeltaPath.Root, Obj(), Obj(("x", Int(1))))
      ], [
        new ArrayMatching(authors, ArrayMatchingMode.ByKey, "id", null),
        new ArrayMatching(DeltaPath.Parse("tags"), ArrayMatchingMode.ByPosition, "name",
          "element [1] on the left has no 'name'")
      ]);

    var read = RoundTrip(delta);

    Assert.Equal(delta.Elements.Count, read.Elements.Count);
    for (var i = 0; i < delta.Elements.Count; i++) {
      var expected = delta.Elements[i];
      var actual = read.Elements[i];
      Assert.Equal(expected.Path, actual.Path);
      Assert.Equal(expected.Operation, actual.Operation);
      Assert.Equal(expected.OldValue, actual.OldValue);
      Assert.Equal(expected.NewValue, actual.NewValue);
      Assert.Equal(expected.MoveTo, actual.MoveTo);
    }
    Assert.Equal(delta.Matchings, read.Matchings);
    Assert.Equal(ArrayMatchingMode.ByKey, read.MatchingFor(authors));
    Assert.Equal(ArrayMatchingMode.ByPosition, read.MatchingFor(DeltaPath.Parse("tags")));
    Assert.Equal(ArrayMatchingMode.ByPosition, read.MatchingFor(DeltaPath.Parse("unlisted")));
  }

  private static DocumentDelta LargeDelta() {
    var text = new string('x', 20 * 1024);
    return new DocumentDelta([
      DeltaElement.Replace(DeltaPath.Parse("body"), Str("short"), Str(text)),
      DeltaElement.Add(DeltaPath.Parse("note"), Str("added"))
    ]);
  }

  private static void AssertSame(DocumentDelta expected, DocumentDelta actual) {
    Assert.Equal(expected.Elements.Count, actual.Elements.Count);
    for (var i = 0; i < expected.Elements.Count; i++) {
      Assert.Equal(expected.Elements[i].Path, actual.Elements[i].Path);
      Assert.Equal(expected.Elements[i].Operation, actual.Elements[i].Operation);
      Assert.Equal(expected.Elements[i].OldValue, actual.Elements[i].OldValue);
      Assert.Equal(expected.Elements[i].NewValue, actual.Elements[i].NewValue);
    }
  }

  [Fact]
  public void ADeltaHoldingA20KbStringRoundTripsThroughStoredRecordUtilities() {
    var delta = LargeDelta();
    var document = new ObjectDocument();
    var recordId = Ulid.NewUlid();
    document.SetIdentifierValue(new UlidDocumentValue(recordId));
    document.SetValue(Obj(("delta", delta.ToDocumentValue())));
    var header = RecordHeader.ForNewRecord(recordId, 1);

    var bytes = StoredRecordUtilities.ToBytes(header, document);
    Assert.True(bytes.Length > TokkConstants.DefaultPageSize, "the delta should be larger than a page");

    var read = StoredRecordUtilities.FromBuffer(new BufferSlice(bytes));
    Assert.Equal(recordId, read.Header.RecordId);
    var stored = ((ObjectDocumentValue)read.Document.Value).Values["delta"];
    AssertSame(delta, DocumentDelta.FromDocumentValue(stored));
  }

  //A record that carries a document value as it is, so a delta can be stored through the
  //engine without a CLR shape of its own.
  public class DeltaHolder {
    public int Id { get; set; }
    public IDocumentValue Delta { get; set; }
  }

  private sealed class DeltaHolderSerializer : DocumentSerializer<DeltaHolder> {
    protected override IDocumentValue Serialize(object value, Type type) {
      return value is IDocumentValue already ? already : base.Serialize(value, type);
    }

    protected override object Deserialize(IDocumentValue value, Type type) {
      return type == typeof(IDocumentValue) ? value : base.Deserialize(value, type);
    }
  }

  [Fact]
  public void ADeltaHoldingA20KbValueIsStoredThroughAnOverflowChainRatherThanRefused() {
    var delta = LargeDelta();
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.Load();
      db.CreateCollection("DeltaHolder", [
        new ColumnDescriptor("Id", ValueTypeEnum.Int),
        new ColumnDescriptor("Delta", ValueTypeEnum.Array)
      ]);
      db.Entities(new DeltaHolderSerializer(), "DeltaHolder")
        .Insert(new DeltaHolder { Id = 1, Delta = delta.ToDocumentValue() });
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var holder = Assert.Single(reopened.Entities(new DeltaHolderSerializer(), "DeltaHolder").GetAll());
    Assert.Equal(1, holder.Id);
    AssertSame(delta, DocumentDelta.FromDocumentValue(holder.Delta));
  }
}
