using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RP-7, RP-8 and N-6: the report agrees with a scan, a clean history verifies, and each kind
//of deliberate damage is reported at the right node.
public class HistoryVerificationTests {
  private const string Collection = "Doc";

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Collection, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Body", ValueTypeEnum.String)]);
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, 8, 1.0);
    return db;
  }

  private static Dictionary<string, IDocumentValue> Doc(string title) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["Body"] = new StringDocumentValue(new string('b', 300))
    };
  }

  //A record with four versions: v1 an Insert root holding its image, v2 and v3 deltas, v4 the head.
  private static (Ulid Id, List<Ulid> Versions) FourVersions(DbEntities<Dictionary<string, IDocumentValue>> docs) {
    var id = docs.Insert(Doc("v1"));
    var versions = new List<Ulid> { docs.HeadVersion(id) };
    foreach (var title in new[] { "v2", "v3", "v4" }) {
      docs.Update(id, Doc(title));
      versions.Add(docs.HeadVersion(id));
    }
    return (id, versions);
  }

  private static VersionNode Copy(VersionNode node, DocumentDelta delta = null, int? distance = null) {
    return new VersionNode {
      RecordId = node.RecordId, VersionId = node.VersionId, Kind = node.Kind, Parent = node.Parent, OperationId = node.OperationId,
      SchemaVersion = node.SchemaVersion, Distance = distance ?? node.Distance, Delta = delta ?? node.Delta, Image = node.Image,
      ImageSchemaVersion = node.ImageSchemaVersion, ReplacedHead = node.ReplacedHead, CutFrom = node.CutFrom
    };
  }

  //Writes a node again, damaged: what a corrupted stored value looks like to the store.
  private static void Damage(TokkDbConnection db, Ulid id, Ulid version, Func<VersionNode, VersionNode> damage) {
    var store = (VersionStore)db.Versions;
    db.InTransaction(() => {
      var node = store.Node(Collection, id, version);
      store.Remove(Collection, id, [version]);
      store.WriteNode(Collection, damage(node));
    });
  }

  [Fact]
  public void TheReportAgreesWithAScanAndACleanHistoryVerifies() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var ids = new List<Ulid>();
    for (var i = 0; i < 5; i++) {
      ids.Add(FourVersions(docs).Id);
    }
    docs.Delete(ids[0]);
    db.InTransaction(() => {
      docs.Update(ids[1], Doc("same operation"));
      docs.Update(ids[2], Doc("same operation"));
    });

    var report = db.HistoryReport(Collection);

    var nodes = ids.SelectMany(id => db.Versions.Nodes(Collection, id)).ToList();
    Assert.Equal(5, report.Records);
    Assert.Equal(nodes.Count, report.Nodes);
    Assert.Equal(nodes.Count(node => node.IsKeyframe), report.Keyframes);
    Assert.Equal(nodes.Select(node => node.OperationId).Distinct().Count(), report.Operations);
    Assert.Equal(nodes.Where(node => node.Delta is not null).Sum(node => CanonicalValue.Bytes(node.Delta.ToDocumentValue()).Length), report.DeltaBytes);
    Assert.Equal(nodes.Where(node => node.Image is not null).Sum(node => CanonicalValue.Bytes(node.Image).Length), report.ImageBytes);
    Assert.True(report.ImageBytes > 0);
    Assert.True(report.DeltaBytes > 0);
    Assert.Equal((ushort)1, report.OldestSchemaVersion);
    var historyName = HistoryCollections.NameFor(db.Collection(Collection).Id);
    Assert.Equal(nodes.Count + report.Operations + db.Versions.SchemaNodes(Collection).Count,
      db.SystemDocuments.ReadAll(historyName).Count());
    Assert.True(report.DataPages > 0);
    Assert.Equal(((VersionStore)db.Versions).VersionIndex(Collection).Nodes().Count(), report.IndexPages);
    Assert.Equal(report.DataPages + report.IndexPages, report.Pages);

    var verification = db.VerifyHistory(Collection);
    Assert.True(verification.IsSound, verification.ToString());
    Assert.Null(verification.FailingVersion);
    Assert.Equal(nodes.Count, verification.Nodes);
  }

  //N-6: a corrupted stored old value raises DeltaMismatchException naming the version and the
  //path, and VerifyHistory reports the node.
  [Fact]
  public void ACorruptedOldValueIsNamedOnReadAndReportedAtItsNode() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var (id, versions) = FourVersions(docs);
    var third = db.Versions.Node(Collection, id, versions[2]);
    Assert.NotNull(third.Delta);
    //A delta whose old value is not what v2 held: computed from a document that never was.
    var wrongBefore = new FieldMapSerializer().Create(Doc("never-v2"), id);
    var after = new FieldMapSerializer().Create(Doc("v3"), id);
    var damaged = DocumentDiff.Compute(wrongBefore.Value, after.Value);
    Damage(db, id, versions[2], node => Copy(node, delta: damaged));

    var mismatch = Assert.Throws<DeltaMismatchException>(() => docs.GetAsOf(id, versions[2]));
    Assert.Equal(versions[2], mismatch.Version);
    Assert.Equal("Title", mismatch.Path.Render());
    Assert.Equal("v2", ((StringDocumentValue)docs.GetAsOf(id, versions[1]).Value["Title"]).Value);
    Assert.Equal("v4", ((StringDocumentValue)docs.GetById(id).Value["Title"]).Value);

    var verification = db.VerifyHistory(Collection);
    Assert.False(verification.IsSound);
    Assert.Equal(versions[2], verification.FailingVersion);
    Assert.Equal(id, verification.FailingRecord);
    Assert.Contains("Title", verification.FirstProblem);
  }

  [Fact]
  public void AStoredDistanceSmallerThanTheActualOneIsReportedAtItsNode() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var (id, versions) = FourVersions(docs);
    Assert.Equal(2, db.Versions.Node(Collection, id, versions[2]).Distance);
    Damage(db, id, versions[2], node => Copy(node, distance: 1));

    //Reading still works — the walk finds the image — but the promise the distance makes is broken.
    Assert.Equal("v3", ((StringDocumentValue)docs.GetAsOf(id, versions[2]).Value["Title"]).Value);
    var verification = db.VerifyHistory(Collection);
    Assert.False(verification.IsSound);
    Assert.Equal(versions[2], verification.FailingVersion);
    Assert.Contains("beyond its stored distance 1", verification.FirstProblem);

    //A larger stored distance, which a purge leaves behind, is allowed.
    Damage(db, id, versions[2], node => Copy(node, distance: 5));
    Assert.True(db.VerifyHistory(Collection).IsSound, db.VerifyHistory(Collection).ToString());
  }

  [Fact]
  public void ABrokenInvariantIsReportedAtItsNode() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var docs = db.Entities(new FieldMapSerializer(), Collection);
    var (id, versions) = FourVersions(docs);
    //WV-10 invariant 2: the node's index entry is taken away.
    var store = (VersionStore)db.Versions;
    db.InTransaction(() => Assert.True(store.VersionIndex(Collection).Delete(CompositeKey.Encode(id, versions[1]))));

    var verification = db.VerifyHistory(Collection);
    Assert.False(verification.IsSound);
    Assert.Equal(versions[1], verification.FailingVersion);
    Assert.Contains("no index entry", verification.FirstProblem);

    //WV-10 invariant 4, on a fresh record: a keyframe that left the head without its image.
    using var other = new TempDatabaseFile();
    using var db2 = NewDatabase(other);
    var docs2 = db2.Entities(new FieldMapSerializer(), Collection);
    var (id2, versions2) = FourVersions(docs2);
    Damage(db2, id2, versions2[0], node => new VersionNode {
      RecordId = node.RecordId, VersionId = node.VersionId, Kind = node.Kind, OperationId = node.OperationId,
      SchemaVersion = node.SchemaVersion, Distance = 0
    });
    var second = db2.VerifyHistory(Collection);
    Assert.False(second.IsSound);
    Assert.Equal(versions2[0], second.FailingVersion);
    Assert.Contains("holds no image", second.FirstProblem);
  }
}
