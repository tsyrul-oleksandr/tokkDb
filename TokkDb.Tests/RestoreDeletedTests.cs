using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RB-2, RB-3, S-1 and N-5: a deleted record comes back under its own identity, into every
//index; a version the index no longer holds is refused; and the §2.4 scenario end to end.
public class RestoreDeletedTests {
  private const string Collection = nameof(Person);

  private static Person Aged(int age) {
    return new Person { Id = 1, Name = "Person-1", Age = age, Passport = new Passport("ST-1"), Tags = [new Tag("t")] };
  }

  [Fact]
  public void ADeletedRecordComesBackUnderItsIdentityAndIntoEveryIndex() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    db.CreateIndex(Collection, "Age");
    db.SetRetentionPolicy(Collection, RetentionPolicy.KeepVersions, 8, 1.0);
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(1));
    people.Update(id, Aged(2));
    var second = people.HeadVersion(id);
    people.Delete(id);
    var tombstone = people.HeadVersion(id);
    Assert.Null(people.GetById(id));

    var result = people.Restore(id, second);

    Assert.True(result.WasDeleted);
    Assert.Equal(tombstone, result.ReplacedHead);
    var record = Assert.Single(people.GetAllRecords());
    Assert.Equal(id, record.RecordId);
    Assert.Equal(2, record.Value.Age);
    Assert.Equal(id, Assert.Single(people.GetBy("Age", 2)).RecordId);
    Assert.NotNull(db.PrimaryIndex(Collection).Find(KeyEncoder.Encode(id).Bytes));
    Assert.Equal(1u, db.Collection(Collection).RecordCount);
    Assert.Equal([VersionKind.Insert, VersionKind.Update, VersionKind.Delete, VersionKind.Restore],
      people.History(id).Versions.Select(version => version.Kind));
    Assert.False(people.History(id).IsDeleted);
    Assert.Equal(second, db.Versions.Node(Collection, id, result.NewVersion).Parent);
    Assert.True(db.Versions.Verify(Collection).IsSound, db.Versions.Verify(Collection).ToString());
  }

  //A version whose index entry is gone reads as no longer kept: the typed refusal of N-3,
  //here with the entry taken out by hand, since a purge arrives at step 7.1.
  [Fact]
  public void AVersionTheIndexNoLongerHoldsIsRefused() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var people = db.Entities<Person>();
    var id = people.Insert(Aged(1));
    var first = people.HeadVersion(id);
    people.Update(id, Aged(2));
    var store = (VersionStore)db.Versions;
    db.InTransaction(() => Assert.True(store.VersionIndex(Collection).Delete(CompositeKey.Encode(id, first))));

    var refusal = Assert.Throws<RestoreRefusedException>(() => people.Restore(id, first));
    Assert.Equal(RestoreRefusal.NoLongerKept, refusal.Reason);
    Assert.Equal(2, people.GetById(id).Value.Age);
  }

  private static Dictionary<string, IDocumentValue> Publication(string title, int year, params string[] authors) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title),
      ["Year"] = new IntDocumentValue(year),
      ["Authors"] = new ArrayDocumentValue(authors.Select(IDocumentValue (author) =>
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue(author) })).ToArray())
    };
  }

  //S-1, the §2.4 scenario, end to end inside an attribution scope.
  [Fact]
  public void TheReferenceScenarioPasses() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    const string name = "Publication";
    db.CreateCollection(name, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("Year", ValueTypeEnum.Int),
      new ColumnDescriptor("Authors", ValueTypeEnum.Array)]);
    var docs = db.Entities(new FieldMapSerializer(), name);
    var cause = Ulid.NewUlid();
    var states = new List<(Ulid Version, Dictionary<string, IDocumentValue> Document)>();
    Ulid id;
    using (db.Attribute(new VersionAttribution("olexander", cause, "the §2.4 scenario"))) {
      var inserted = Publication("Delta versoning", 2025, "Ann", "Cid");
      id = docs.Insert(inserted);
      states.Add((docs.HeadVersion(id), inserted));
      var corrected = Publication("Delta versioning", 2025, "Ann", "Cid");
      docs.Update(id, corrected);
      states.Add((docs.HeadVersion(id), corrected));
      var extended = Publication("Delta versioning", 2026, "Ann", "Bob", "Cid");
      docs.Update(id, extended);
      states.Add((docs.HeadVersion(id), extended));
      docs.Delete(id);
      states.Add((docs.HeadVersion(id), null));
      var result = docs.Restore(id, states[2].Version);
      states.Add((result.NewVersion, extended));
    }

    var history = docs.History(id);
    Assert.Equal([VersionKind.Insert, VersionKind.Update, VersionKind.Update, VersionKind.Delete, VersionKind.Restore],
      history.Versions.Select(version => version.Kind));
    Assert.Equal(5, history.Versions.Select(version => version.OperationId).Distinct().Count());
    Assert.All(history.Versions, version => {
      Assert.Equal("olexander", version.Author);
      Assert.Equal(cause, version.Cause);
      Assert.Equal("the §2.4 scenario", version.Comment);
      Assert.True(version.RecordedAt > DateTime.UtcNow.AddMinutes(-1));
    });
    Assert.Equal(states.Select(state => state.Version), history.Versions.Select(version => version.VersionId));

    //GetAsOf returns each state, by version and by moment.
    foreach (var (version, document) in states) {
      var read = docs.GetAsOf(id, version);
      if (document is null) {
        Assert.True(read.IsDeleted);
        Assert.Equal(AsOfOutcome.Deleted, docs.GetAsOf(id, version.Time).Outcome);
        continue;
      }
      Assert.True(CanonicalValue.Equal(new FieldMapSerializer().Create(document, id).Value, new FieldMapSerializer().Create(read.Value, id).Value));
    }

    //Diff between the two updates: one Replace and one Insert.
    var diff = docs.Diff(id, states[1].Version, states[2].Version);
    Assert.Equal(2, diff.Delta.Elements.Count);
    Assert.Contains(diff.Delta.Elements, element => element.Operation == DeltaOperation.Replace && element.Path.Render() == "Year");
    Assert.Contains(diff.Delta.Elements, element => element.Operation == DeltaOperation.Insert && element.Path.Render() == "Authors[1]");
    //At the default ratio a publication this small makes the third version a keyframe, so the
    //diff comes from two reconstructions rather than the stored delta; either way it is the same.

    //The restored record has its original identity, and is the record.
    var record = Assert.Single(docs.GetAllRecords());
    Assert.Equal(id, record.RecordId);
    Assert.Equal("Delta versioning", ((StringDocumentValue)record.Value["Title"]).Value);
    Assert.Equal(states[2].Version, db.Versions.Node(name, id, states[4].Version).Parent);
    Assert.True(db.Versions.Verify(name).IsSound, db.Versions.Verify(name).ToString());
  }
}
