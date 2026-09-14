using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RH-4, N-13 and G-6: a diff between versions, from the stored delta where there is one, mapped
//through the schema, and never converting what it cannot carry.
public class VersionDiffTests {
  private const string Name = "Doc";

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Name, [
      new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)
    ]);
    db.SetRetentionPolicy(Name, RetentionPolicy.KeepVersions, snapshotInterval: 8, largeDeltaRatio: 1.0);
    return db;
  }

  //A body large enough that a one-field change is a small delta rather than a keyframe.
  private static readonly string Body = new('x', 400);

  private static Dictionary<string, IDocumentValue> Doc(string title, IDocumentValue year, string note = "n", string yearColumn = "Year") {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), [yearColumn] = year, ["Note"] = new StringDocumentValue(note),
      ["Body"] = new StringDocumentValue(Body)
    };
  }

  [Fact]
  public void AParentAndChildDiffComesFromTheStoredDeltaAndReadsNoImage() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Name);
    var id = docs.Insert(Doc("a", new IntDocumentValue(2000)));
    var first = docs.HeadVersion(id);
    docs.Update(id, Doc("b", new IntDocumentValue(2000)));
    var second = docs.HeadVersion(id);
    var height = ((VersionStore)db.Versions).VersionIndex(Name).Height();

    var diff = docs.Diff(id, first, second);

    Assert.True(diff.FromStoredDelta);
    Assert.True(diff.PagesRead <= height + 1, $"{diff.PagesRead} pages read at height {height}");
    var element = Assert.Single(diff.Delta.Elements);
    Assert.Equal("Title", element.Path.Render());
    Assert.Equal(DeltaOperation.Replace, element.Operation);
    Assert.Empty(diff.Unmapped);
    //As stored, the same delta, unmapped.
    Assert.Single(docs.Diff(id, first, second, asStored: true).Delta.Elements);
  }

  [Fact]
  public void ADiffAcrossARenameShowsTheChangeUnderTheNewNameWithNoRemoveAddPair() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Name);
    var id = docs.Insert(Doc("a", new IntDocumentValue(2000)));
    var first = docs.HeadVersion(id);
    docs.Update(id, Doc("a", new IntDocumentValue(2001)));
    var second = docs.HeadVersion(id);
    db.SetColumns(Name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Published", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)],
      [ColumnMigration.Rename(0, "Year", "Published")]);
    docs.Update(id, Doc("a", new IntDocumentValue(2002), yearColumn: "Published"));
    var third = docs.HeadVersion(id);

    var stored = docs.Diff(id, first, second);
    var element = Assert.Single(stored.Delta.Elements);
    Assert.Equal("Published", element.Path.Render());
    Assert.Equal(DeltaOperation.Replace, element.Operation);
    Assert.True(stored.FromStoredDelta);

    //Across the rename, between reconstructions: one Replace under the new name, nothing removed
    //or added for the rename itself.
    var across = docs.Diff(id, first, third);
    Assert.False(across.FromStoredDelta);
    var replaced = Assert.Single(across.Delta.Elements);
    Assert.Equal("Published", replaced.Path.Render());
    Assert.Equal(DeltaOperation.Replace, replaced.Operation);
    Assert.Equal(2000, ((IntDocumentValue)replaced.OldValue.Value).Value);
    Assert.Equal(2002, ((IntDocumentValue)replaced.NewValue.Value).Value);
    Assert.Empty(across.Unmapped);
    //As stored, the rename shows as what it is on the pages.
    var asStored = docs.Diff(id, first, third, asStored: true);
    Assert.Contains(asStored.Delta.Elements, e => e.Operation == DeltaOperation.Remove && e.Path.Render() == "Year");
    Assert.Contains(asStored.Delta.Elements, e => e.Operation == DeltaOperation.Add && e.Path.Render() == "Published");
  }

  //N-13 and G-6: a lossy retype reports the old values as unmapped instead of converting them,
  //while GetStoredAsOf still shows them.
  [Fact]
  public void ADiffAcrossALossyRetypeReportsTheOldValuesAsUnmapped() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Name);
    var id = docs.Insert(Doc("a", new StringDocumentValue("about 2000")));
    var first = docs.HeadVersion(id);
    docs.Update(id, Doc("a", new StringDocumentValue("about 2001")));
    var second = docs.HeadVersion(id);
    db.SetColumns(Name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)],
      [ColumnMigration.Retype(0, "Year", ValueTypeEnum.Int)]);

    var diff = docs.Diff(id, first, second);
    Assert.True(diff.FromStoredDelta);
    Assert.Empty(diff.Delta.Elements);
    var report = Assert.Single(diff.Unmapped);
    Assert.Equal(UnmappedReason.RetypeLossy, report.Reason);
    Assert.Equal("Year", report.Column);
    Assert.Equal("about 2001", ((StringDocumentValue)report.Value).Value);

    var read = docs.GetAsOf(id, second);
    Assert.False(read.Value.ContainsKey("Year"));
    Assert.Equal(UnmappedReason.RetypeLossy, Assert.Single(read.Unmapped).Reason);
    Assert.Equal("about 2001", ((StringDocumentValue)((ObjectDocumentValue)docs.GetStoredAsOf(id, second).Document.Value).Values["Year"]).Value);
    //As stored, nothing is unmapped and the strings are there.
    Assert.Single(docs.Diff(id, first, second, asStored: true).Delta.Elements);
  }

  //Two leaves have no stored delta between them: the diff equals Compute over their mapped
  //reconstructions.
  [Fact]
  public void ADiffBetweenTwoLeavesEqualsComputeOverTheirReconstructions() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Name);
    var id = docs.Insert(Doc("a", new IntDocumentValue(2000)));
    docs.Update(id, Doc("b", new IntDocumentValue(2001)));
    var second = docs.HeadVersion(id);
    docs.Update(id, Doc("c", new IntDocumentValue(2002), note: "m"));
    var third = docs.HeadVersion(id);
    //A second branch off the second version, written through the store by hand.
    var store = (VersionStore)db.Versions;
    var parent = db.Versions.Node(Name, id, second);
    var branchDoc = new FieldMapSerializer().Create(Doc("z", new IntDocumentValue(2001), note: "branch"), id);
    var branchVersion = Pages.Records.RecordIdentity.Next();
    db.InTransaction(() => store.WriteNode(Name, new VersionNode {
      RecordId = id, VersionId = branchVersion, Kind = VersionKind.Restore, Parent = second, OperationId = parent.OperationId,
      SchemaVersion = 1, Distance = parent.Distance + 1,
      Delta = DocumentDiff.Compute(db.Versions.Reconstruct(Name, id, second).Document.Value, branchDoc.Value)
    }));

    var diff = docs.Diff(id, third, branchVersion);
    Assert.False(diff.FromStoredDelta);
    var expected = DocumentDiff.Compute(db.Versions.Reconstruct(Name, id, third).Document.Value,
      db.Versions.Reconstruct(Name, id, branchVersion).Document.Value);
    Assert.Equal(expected.Elements.Select(e => e.ToString()), diff.Delta.Elements.Select(e => e.ToString()));
    //Title, Year and Note all differ between the two leaves.
    Assert.Equal(3, diff.Delta.Elements.Count);
  }

  //WV-8 with the mapping in place: Rewrite stores an image only for heads that have something
  //unmapped — a lossless retype alone gains nothing.
  [Fact]
  public void RewriteStoresAnImageOnlyForHeadsWithSomethingUnmapped() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Name);
    var clean = docs.Insert(Doc("a", new IntDocumentValue(2000)));
    var lossy = docs.Insert(Doc("b", new IntDocumentValue(2001), note: "keep"));
    db.SetColumns(Name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Long),
      new ColumnDescriptor("Note", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)],
      [ColumnMigration.Retype(0, "Year", ValueTypeEnum.Long)]);
    Assert.Equal(2, db.Rewrite(Name));
    Assert.Null(db.Versions.Head(Name, clean).Image);
    Assert.Null(db.Versions.Head(Name, lossy).Image);

    db.SetColumns(Name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Long),
      new ColumnDescriptor("Body", ValueTypeEnum.String)], [ColumnMigration.Remove(0, "Note")]);
    //Every record holds a Note, so every head has something unmapped and gains its image.
    Assert.Equal(2, db.Rewrite(Name));
    Assert.NotNull(db.Versions.Head(Name, clean).Image);
    Assert.NotNull(db.Versions.Head(Name, lossy).Image);
    Assert.Equal("keep", ((StringDocumentValue)db.Versions.Head(Name, lossy).Image.Values["Note"]).Value);
    var read = docs.GetAsOf(lossy, docs.HeadVersion(lossy));
    Assert.False(read.Value.ContainsKey("Note"));
    Assert.Equal(UnmappedReason.ColumnRemoved, Assert.Single(read.Unmapped).Reason);
  }
}
