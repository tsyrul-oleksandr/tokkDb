using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//RH-5, RH-7, RH-8, WV-5 and V-3: any version comes back as stored, from the nearest image
//forward, within the bound, under a catalogue lease.
public class ReconstructionTests(ITestOutputHelper output) {
  private const string Collection = nameof(Person);
  private static readonly DocumentSerializer<Person> Serializer = new();

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, int k = 8, double ratio = 0.5) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, k, ratio);
    return db;
  }

  private static Person Small(int step) {
    return new Person { Id = 1, Name = "Person-1", Age = 20 + step, Passport = new Passport("ST-1"), Tags = [new Tag("t")] };
  }

  private static Person Rewritten(int step) {
    return new Person {
      Id = 1000 + step, Name = $"Rewritten-{step}", Age = 60 + step, Passport = new Passport($"RW-{step}"),
      Tags = [new Tag($"a-{step}"), new Tag($"b-{step}")]
    };
  }

  private static void AssertSame(IDocumentValue expected, IDocumentValue actual, string context) {
    Assert.True(CanonicalValue.Equal(expected, actual), $"{context}: expected {CanonicalValue.Describe(expected)} but reconstructed {CanonicalValue.Describe(actual)}");
  }

  [Fact]
  public void TheHeadReadsItsLiveImageAndAnOlderVersionComesFromItsKeyframe() {
    using var file = new TempDatabaseFile();
    //The ratio at 1: a one-field change to a record this small is otherwise half its size.
    using var db = NewDatabase(file, k: 8, ratio: 1.0);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(0));
    var captured = new List<(Ulid Version, ObjectDocument Document)> { (people.HeadVersion(id), Serializer.Create(Small(0), id)) };
    for (var step = 1; step <= 5; step++) {
      people.Update(id, Small(step));
      captured.Add((people.HeadVersion(id), Serializer.Create(Small(step), id)));
    }

    var head = db.Versions.Reconstruct(Collection, id, captured[^1].Version);
    AssertSame(captured[^1].Document.Value, head.Document.Value, "head");
    Assert.Equal(1, head.Report.VersionsExamined);
    Assert.Equal(0, head.Report.DeltasApplied);
    Assert.Equal(captured[^1].Version, head.Report.Keyframe);

    var third = db.Versions.Reconstruct(Collection, id, captured[3].Version);
    AssertSame(captured[3].Document.Value, third.Document.Value, "version 3");
    //Distance 3 from the root keyframe: four versions examined, three deltas applied.
    Assert.Equal(4, third.Report.VersionsExamined);
    Assert.Equal(3, third.Report.DeltasApplied);
    Assert.Equal(captured[0].Version, third.Report.Keyframe);
    Assert.True(third.Report.PagesRead > 0);
    Assert.False(third.IsDeleted);
    Assert.Equal(1, third.SchemaVersion);
  }

  //WV-5: for four intervals, over 200 updates with complete rewrites among them, plus a branch
  //built by hand off an old version, no reconstruction applies more than k - 1 deltas — and
  //every one of them gives back what was written.
  [Theory]
  [InlineData(1)]
  [InlineData(4)]
  [InlineData(8)]
  [InlineData(32)]
  public void NoReconstructionAppliesMoreThanKMinusOneDeltas(int k) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, k, 0.5);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(0));
    var captured = new List<(Ulid Version, ObjectDocument Document)> { (people.HeadVersion(id), Serializer.Create(Small(0), id)) };
    for (var step = 1; step <= 200; step++) {
      var value = step % 37 == 0 ? Rewritten(step) : Small(step);
      people.Update(id, value);
      captured.Add((people.HeadVersion(id), Serializer.Create(value, id)));
    }

    //A second branch: a restore-like child of version 50, built by hand through the store's
    //node writer, holding either a delta from its parent or, when the count rule says so, an
    //image (WV-10). It is not the head, so the live image is not involved.
    var store = (VersionStore)db.Versions;
    var parent = db.Versions.Node(Collection, id, captured[50].Version);
    var branched = Serializer.Create(new Person { Id = 1, Name = "Branch", Age = 999, Passport = new Passport("B"), Tags = [] }, id);
    var branchVersion = Pages.Records.RecordIdentity.Next();
    var distance = parent.Distance + 1 >= k ? 0 : parent.Distance + 1;
    db.InTransaction(() => store.WriteNode(Collection, new VersionNode {
      RecordId = id, VersionId = branchVersion, Kind = VersionKind.Restore, Parent = parent.VersionId,
      OperationId = parent.OperationId, SchemaVersion = 1, Distance = distance,
      Delta = distance == 0 ? null : DocumentDiff.Compute(captured[50].Document.Value, branched.Value),
      Image = distance == 0 ? (ObjectDocumentValue)branched.Value : null, ImageSchemaVersion = 1
    }));
    captured.Add((branchVersion, branched));

    var worst = 0;
    foreach (var (version, document) in captured) {
      var reconstruction = db.Versions.Reconstruct(Collection, id, version);
      AssertSame(document.Value, reconstruction.Document.Value, $"version {version} at k = {k}");
      Assert.True(reconstruction.Report.DeltasApplied <= k - 1,
        $"k = {k}: version {version} applied {reconstruction.Report.DeltasApplied} deltas ({reconstruction.Report})");
      worst = Math.Max(worst, reconstruction.Report.DeltasApplied);
    }
    output.WriteLine($"k = {k}: at most {worst} deltas applied over {captured.Count} versions");
    if (k == 1) {
      Assert.Equal(0, worst);
    }
  }

  private static List<ColumnDescriptor> Columns(params (string Name, ValueTypeEnum Type)[] columns) {
    return columns.Select(column => new ColumnDescriptor(column.Name, column.Type)).ToList();
  }

  private static Dictionary<string, IDocumentValue> Fields(params (string Name, IDocumentValue Value)[] fields) {
    return fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
  }

  //RH-5: updates interleaved with a rename, a retype and a removal, then Rewrite. Every version
  //reconstructs to what was written, migrated to the schema version the reconstruction reports.
  [Fact]
  public void AHistoryAcrossSchemaChangesReconstructsEveryVersionEvenAfterRewrite() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    const string name = "Doc";
    db.CreateCollection(name, Columns(("Title", ValueTypeEnum.String), ("Year", ValueTypeEnum.Int), ("Note", ValueTypeEnum.String)));
    db.SetRetentionPolicy(name, RetentionPolicy.KeepVersions, snapshotInterval: 3);
    var serializer = new FieldMapSerializer();
    var docs = db.Entities(serializer, name);
    var captured = new List<(Ulid Version, ObjectDocument Document, ushort Schema)>();

    void Write(Dictionary<string, IDocumentValue> fields, Ulid? existing = null) {
      var id = existing ?? docs.Insert(fields);
      if (existing is not null) {
        docs.Update(id, fields);
      }
      captured.Add((docs.HeadVersion(id), serializer.Create(fields, id), db.Collection(name).SchemaVersion));
    }

    Write(Fields(("Title", new StringDocumentValue("a")), ("Year", new IntDocumentValue(2000)), ("Note", new StringDocumentValue("n"))));
    var id = docs.GetAllRecords().Single().RecordId;
    //A second record written at the first schema and never touched again: the one Rewrite
    //has to migrate, and whose pre-rewrite image WV-8 keeps.
    var untouched = Fields(("Title", new StringDocumentValue("u")), ("Year", new IntDocumentValue(1999)), ("Note", new StringDocumentValue("keep")));
    var untouchedId = docs.Insert(untouched);
    var untouchedVersion = docs.HeadVersion(untouchedId);
    Write(Fields(("Title", new StringDocumentValue("b")), ("Year", new IntDocumentValue(2000)), ("Note", new StringDocumentValue("n"))), id);
    //Add a column (no migration step).
    db.SetColumns(name, Columns(("Title", ValueTypeEnum.String), ("Year", ValueTypeEnum.Int), ("Note", ValueTypeEnum.String), ("Rating", ValueTypeEnum.Int)));
    Write(Fields(("Title", new StringDocumentValue("b")), ("Year", new IntDocumentValue(2000)), ("Note", new StringDocumentValue("n")), ("Rating", new IntDocumentValue(5))), id);
    //Rename.
    db.SetColumns(name, Columns(("Title", ValueTypeEnum.String), ("Published", ValueTypeEnum.Int), ("Note", ValueTypeEnum.String), ("Rating", ValueTypeEnum.Int)),
      [ColumnMigration.Rename(0, "Year", "Published")]);
    Write(Fields(("Title", new StringDocumentValue("b")), ("Published", new IntDocumentValue(2001)), ("Note", new StringDocumentValue("n")), ("Rating", new IntDocumentValue(5))), id);
    //Lossless retype.
    db.SetColumns(name, Columns(("Title", ValueTypeEnum.String), ("Published", ValueTypeEnum.Long), ("Note", ValueTypeEnum.String), ("Rating", ValueTypeEnum.Int)),
      [ColumnMigration.Retype(0, "Published", ValueTypeEnum.Long)]);
    Write(Fields(("Title", new StringDocumentValue("c")), ("Published", new LongDocumentValue(2001)), ("Note", new StringDocumentValue("n")), ("Rating", new IntDocumentValue(5))), id);
    //Removal.
    db.SetColumns(name, Columns(("Title", ValueTypeEnum.String), ("Published", ValueTypeEnum.Long), ("Rating", ValueTypeEnum.Int)),
      [ColumnMigration.Remove(0, "Note")]);
    Write(Fields(("Title", new StringDocumentValue("d")), ("Published", new LongDocumentValue(2001)), ("Rating", new IntDocumentValue(6))), id);
    Write(Fields(("Title", new StringDocumentValue("e")), ("Published", new LongDocumentValue(2002)), ("Rating", new IntDocumentValue(6))), id);

    void AssertEveryVersion(string when) {
      foreach (var (version, document, schema) in captured) {
        var reconstruction = db.Versions.Reconstruct(name, id, version);
        Assert.True(reconstruction.SchemaVersion >= schema,
          $"{when}: version {version} written at schema {schema} reconstructed at schema {reconstruction.SchemaVersion} " +
          $"({reconstruction.Node.Kind} d{reconstruction.Node.Distance} node schema {reconstruction.Node.SchemaVersion}, {reconstruction.Report})");
        var expected = SchemaMigrator.FromSteps(db.Versions.SchemaAt(name, schema, reconstruction.SchemaVersion)).Apply(document);
        AssertSame(expected.Value, reconstruction.Document.Value, $"{when}: version {version} written at schema {schema}");
      }
    }

    AssertEveryVersion("before Rewrite");
    //Only the untouched record is below the current version; the other was rewritten by its updates.
    Assert.Equal(1, db.Rewrite(name));
    Assert.Empty(db.Collection(name).Migrations);
    AssertEveryVersion("after Rewrite");
    //The untouched record's first version reconstructs as written, from the image Rewrite kept.
    var kept = db.Versions.Reconstruct(name, untouchedId, untouchedVersion);
    AssertSame(serializer.Create(untouched, untouchedId).Value, kept.Document.Value, "the untouched record after Rewrite");
    Assert.Equal(1, kept.SchemaVersion);
    Assert.True(db.Versions.Verify(name).IsSound, db.Versions.Verify(name).ToString());
  }

  //RH-7: a schema change started while a reconstruction runs waits for it.
  [Fact]
  public void ASetColumnsStartedMidReconstructionWaits() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, k: 8);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(0));
    var first = people.HeadVersion(id);
    for (var step = 1; step <= 3; step++) {
      people.Update(id, Small(step));
    }
    var store = (VersionStore)db.Versions;
    using var inside = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    store.ReconstructionProbe = _ => {
      inside.Set();
      release.Wait(TimeSpan.FromSeconds(10));
    };

    var reconstruction = Task.Run(() => db.Versions.Reconstruct(Collection, id, first));
    Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the reconstruction did not start");
    var schemaChange = Task.Run(() => {
      var columns = db.Collection(Collection).Columns.ToList();
      columns.Add(new ColumnDescriptor("City", ValueTypeEnum.String));
      db.SetColumns(Collection, columns);
    });
    Assert.False(schemaChange.Wait(TimeSpan.FromMilliseconds(300)), "the schema change did not wait for the reconstruction");

    release.Set();
    Assert.True(reconstruction.Wait(TimeSpan.FromSeconds(10)));
    Assert.True(schemaChange.Wait(TimeSpan.FromSeconds(10)));
    Assert.Equal(20, ((IntDocumentValue)((ObjectDocumentValue)reconstruction.Result.Document.Value).Values["Age"]).Value);
    Assert.Equal(2, db.Collection(Collection).SchemaVersion);
    store.ReconstructionProbe = null;
  }

  [Fact]
  public void AMissingVersionIsRefusedAndATombstoneReconstructsAsDeleted() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file);
    var people = db.Entities<Person>();
    var id = people.Insert(Small(0));
    Assert.Throws<VersionNotFoundException>(() => db.Versions.Reconstruct(Collection, id, Ulid.NewUlid()));
    people.Delete(id);
    var tombstone = db.Versions.Reconstruct(Collection, id, people.HeadVersion(id));
    Assert.True(tombstone.IsDeleted);
    Assert.Null(tombstone.Document);
    //And the version before it still reads, from its keyframe's image.
    var last = db.Versions.Nodes(Collection, id)[0];
    AssertSame(Serializer.Create(Small(0), id).Value, db.Versions.Reconstruct(Collection, id, last.VersionId).Document.Value, "before the delete");
  }
}
