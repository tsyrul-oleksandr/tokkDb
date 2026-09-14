using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//RB-5, V-16, S-9 and N-11: a related restore follows outgoing relations as declared at the
//moment, verifies every holder, and refuses whole before anything is written.
[Collection(EngineClockCollection.Name)]
public class RelatedRestoreTests {
  private const string Publications = "Publication";
  private const string Authors = "Author";
  private const string Venues = "Venue";

  private sealed class SteppingClock : IDisposable {
    public SteppingClock() {
      RecordIdentity.ResetForTests();
      Now = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero);
    }

    public DateTimeOffset Now { get; private set; }
    public DateTimeOffset Read() => Now = Now.AddSeconds(1);
    public DateTimeOffset Between => Now.AddMilliseconds(500);

    public void Dispose() {
      RecordIdentity.ResetForTests();
    }
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Authors, [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true), new ColumnDescriptor("Affiliation", ValueTypeEnum.String)]);
    db.CreateCollection(Publications, [new ColumnDescriptor("Title", ValueTypeEnum.String), new ColumnDescriptor("AuthorName", ValueTypeEnum.String),
      new ColumnDescriptor("VenueName", ValueTypeEnum.String)]);
    db.CreateRelation("PublicationAuthor", Publications, "AuthorName", Authors, "Name");
    return db;
  }

  private static Dictionary<string, IDocumentValue> Author(string name, string affiliation) {
    return new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Name"] = new StringDocumentValue(name), ["Affiliation"] = new StringDocumentValue(affiliation)
    };
  }

  private static Dictionary<string, IDocumentValue> Publication(string title, string author, string venue = null) {
    var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
      ["Title"] = new StringDocumentValue(title), ["AuthorName"] = new StringDocumentValue(author)
    };
    if (venue is not null) {
      document["VenueName"] = new StringDocumentValue(venue);
    }
    return document;
  }

  private static string Text(DbEntities<Dictionary<string, IDocumentValue>> docs, Ulid id, string column) {
    return ((StringDocumentValue)docs.GetById(id).Value[column]).Value;
  }

  //S-9: a publication and its author, both edited since a moment, are restored together to that
  //moment; a relation created after the moment is not followed.
  [Fact]
  public void APublicationAndItsAuthorAreRestoredTogetherAndALaterRelationIsNotFollowed() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var authors = db.Entities(new FieldMapSerializer(), Authors);
    var publications = db.Entities(new FieldMapSerializer(), Publications);
    var ann = authors.Insert(Author("Ann", "Lviv"));
    var paper = publications.Insert(Publication("Deltas", "Ann"));
    var moment = clock.Between;
    authors.Update(ann, Author("Ann", "Kyiv"));
    publications.Update(paper, Publication("Deltas, revised", "Ann"));
    //A relation created after the moment, to a venue edited since: not followed.
    db.CreateCollection(Venues, [new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true), new ColumnDescriptor("City", ValueTypeEnum.String)]);
    var venues = db.Entities(new FieldMapSerializer(), Venues);
    var venue = venues.Insert(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("VLDB"), ["City"] = new StringDocumentValue("Rome") });
    db.CreateRelation("PublicationVenue", Publications, "VenueName", Venues, "Name");
    publications.Update(paper, Publication("Deltas, revised", "Ann", "VLDB"));
    venues.Update(venue, new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("VLDB"), ["City"] = new StringDocumentValue("Milan") });
    var venueHead = venues.HeadVersion(venue);

    var result = publications.RestoreAsOf(paper, moment);

    Assert.Equal(2, result.Restored.Count);
    Assert.Equal([Publications, Authors], result.Restored.Select(record => record.CollectionName));
    Assert.Equal("Deltas", Text(publications, paper, "Title"));
    Assert.False(publications.GetById(paper).Value.ContainsKey("VenueName"));
    Assert.Equal("Lviv", Text(authors, ann, "Affiliation"));
    Assert.Equal(venueHead, venues.HeadVersion(venue));
    Assert.Equal("Milan", Text(venues, venue, "City"));
    Assert.Equal(VersionKind.Restore, publications.History(paper).Versions[^1].Kind);
    Assert.Equal(VersionKind.Restore, authors.History(ann).Versions[^1].Kind);
    //One transaction: one operation for both restores.
    Assert.Equal(publications.History(paper).Versions[^1].OperationId, authors.History(ann).Versions[^1].OperationId);
    Assert.True(db.VerifyHistory(Publications).IsSound);
    Assert.True(db.VerifyHistory(Authors).IsSound);
  }

  //N-11: a holder that cannot be verified refuses the whole restore, naming the relation and
  //the value, before anything is written.
  [Fact]
  public void AnUnverifiableHolderRefusesTheWholeRestoreBeforeAnythingIsWritten() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var authors = db.Entities(new FieldMapSerializer(), Authors);
    var publications = db.Entities(new FieldMapSerializer(), Publications);
    var bob = authors.Insert(Author("Bob", "Lviv"));
    var paper = publications.Insert(Publication("Trees", "Bob"));
    var moment = clock.Between;
    publications.Update(paper, Publication("Trees, revised", "Bob"));
    var paperHead = publications.HeadVersion(paper);

    //Deleted since: the value has no holder now.
    authors.Insert(Author("Carol", "Odesa"));
    publications.Update(paper, Publication("Trees, revised", "Carol"));
    authors.Delete(bob);
    var refusal = Assert.Throws<RelatedRestoreRefusedException>(() => publications.RestoreAsOf(paper, moment));
    Assert.Equal(RelatedRestoreRefusal.HolderNotFound, refusal.Reason);
    Assert.Equal("PublicationAuthor", refusal.RelationName);
    Assert.Equal("Bob", refusal.Value);
    Assert.Equal("Trees, revised", Text(publications, paper, "Title"));
    Assert.Equal("Carol", Text(publications, paper, "AuthorName"));
    Assert.Equal(3, publications.History(paper).Versions.Count);

    //Somebody else since: a new record holds the value now, but did not at the moment.
    var impostor = authors.Insert(Author("Bob", "Elsewhere"));
    refusal = Assert.Throws<RelatedRestoreRefusedException>(() => publications.RestoreAsOf(paper, moment));
    Assert.Equal(RelatedRestoreRefusal.HolderNotVerified, refusal.Reason);
    Assert.Equal(impostor, refusal.RecordId);
    Assert.Equal("Bob", refusal.Value);
    Assert.Equal(3, publications.History(paper).Versions.Count);
    Assert.Single(authors.History(impostor).Versions);

    //Without following relations, the record alone is restored.
    var alone = publications.RestoreAsOf(paper, moment, followRelations: false);
    Assert.Single(alone.Restored);
    Assert.Equal("Trees", Text(publications, paper, "Title"));
  }

  [Fact]
  public void ACycleTerminatesAndARecordAlreadyAtTheMomentIsLeftAlone() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    //Two collections referring to each other by unique keys: a cycle.
    db.CreateCollection("Left", [new ColumnDescriptor("Key", ValueTypeEnum.String, unique: true), new ColumnDescriptor("Other", ValueTypeEnum.String), new ColumnDescriptor("Note", ValueTypeEnum.String)]);
    db.CreateCollection("Right", [new ColumnDescriptor("Key", ValueTypeEnum.String, unique: true), new ColumnDescriptor("Other", ValueTypeEnum.String), new ColumnDescriptor("Note", ValueTypeEnum.String)]);
    var left = db.Entities(new FieldMapSerializer(), "Left");
    var right = db.Entities(new FieldMapSerializer(), "Right");
    Dictionary<string, IDocumentValue> Doc(string key, string other, string note) => new(StringComparer.Ordinal) {
      ["Key"] = new StringDocumentValue(key), ["Other"] = new StringDocumentValue(other), ["Note"] = new StringDocumentValue(note)
    };
    var l = left.Insert(Doc("L", "R", "l1"));
    var r = right.Insert(Doc("R", "L", "r1"));
    db.CreateRelation("LeftRight", "Left", "Other", "Right", "Key");
    db.CreateRelation("RightLeft", "Right", "Other", "Left", "Key");
    var moment = clock.Between;
    left.Update(l, Doc("L", "R", "l2"));
    var rightHead = right.HeadVersion(r);

    var result = left.RestoreAsOf(l, moment);

    Assert.Single(result.Restored);
    Assert.Equal("Left", result.Restored[0].CollectionName);
    var untouched = Assert.Single(result.AlreadyAtMoment);
    Assert.Equal("Right", untouched.CollectionName);
    Assert.Equal(rightHead, right.HeadVersion(r));
    Assert.Equal("l1", ((StringDocumentValue)left.GetById(l).Value["Note"]).Value);
  }

  [Fact]
  public void ExceedingTheCapFailsBeforeAnythingIsWrittenNamingTheCapAndTheSizeReached() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    //A chain: each node refers to the next by key.
    db.CreateCollection("Node", [new ColumnDescriptor("Key", ValueTypeEnum.String, unique: true), new ColumnDescriptor("Next", ValueTypeEnum.String), new ColumnDescriptor("Note", ValueTypeEnum.String)]);
    var nodes = db.Entities(new FieldMapSerializer(), "Node");
    //The last node of the chain refers to nothing.
    Dictionary<string, IDocumentValue> Doc(int i, string note) {
      var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal) {
        ["Key"] = new StringDocumentValue($"n{i}"), ["Note"] = new StringDocumentValue(note)
      };
      if (i < 6) {
        document["Next"] = new StringDocumentValue($"n{i + 1}");
      }
      return document;
    }
    var ids = new List<Ulid>();
    for (var i = 6; i >= 0; i--) {
      ids.Insert(0, nodes.Insert(Doc(i, "first")));
    }
    db.CreateRelation("NodeNext", "Node", "Next", "Node", "Key");
    var moment = clock.Between;
    foreach (var id in ids) {
      nodes.Update(id, Doc(ids.IndexOf(id), "second"));
    }
    var heads = ids.Select(nodes.HeadVersion).ToList();

    var refusal = Assert.Throws<RelatedRestoreRefusedException>(() => nodes.RestoreAsOf(ids[0], moment, cap: 3));
    Assert.Equal(RelatedRestoreRefusal.CapExceeded, refusal.Reason);
    Assert.Equal(3, refusal.Cap);
    Assert.Equal(4, refusal.Reached);
    Assert.Equal(heads, ids.Select(nodes.HeadVersion));

    //Within the cap, the whole chain: the last node refers to nothing, so the relations end there.
    var result = nodes.RestoreAsOf(ids[0], moment, cap: DbEntities<Dictionary<string, IDocumentValue>>.DefaultRelatedRestoreCap);
    Assert.Equal(7, result.RecordsVisited);
    Assert.All(ids, id => Assert.Equal("first", ((StringDocumentValue)nodes.GetById(id).Value["Note"]).Value));
    Assert.True(db.VerifyHistory("Node").IsSound, db.VerifyHistory("Node").ToString());
  }

  //No record referring to a restored one is changed: incoming relations are not followed.
  [Fact]
  public void ARecordReferringToTheRestoredOneIsNeverChanged() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var authors = db.Entities(new FieldMapSerializer(), Authors);
    var publications = db.Entities(new FieldMapSerializer(), Publications);
    var dan = authors.Insert(Author("Dan", "Lviv"));
    var paper = publications.Insert(Publication("Graphs", "Dan"));
    var moment = clock.Between;
    authors.Update(dan, Author("Dan", "Kyiv"));
    publications.Update(paper, Publication("Graphs, revised", "Dan"));
    var paperHead = publications.HeadVersion(paper);

    var result = authors.RestoreAsOf(dan, moment);

    Assert.Single(result.Restored);
    Assert.Equal("Lviv", Text(authors, dan, "Affiliation"));
    Assert.Equal(paperHead, publications.HeadVersion(paper));
    Assert.Equal("Graphs, revised", Text(publications, paper, "Title"));
  }

  [Fact]
  public void ARecordWithNoVersionAtTheMomentIsRefused() {
    using var file = new TempDatabaseFile();
    using var clock = new SteppingClock();
    using var _ = EngineClock.Override(clock.Read);
    using var db = NewDatabase(file);
    var publications = db.Entities(new FieldMapSerializer(), Publications);
    var authors = db.Entities(new FieldMapSerializer(), Authors);
    authors.Insert(Author("Eve", "Lviv"));
    var before = clock.Between;
    var paper = publications.Insert(Publication("Late", "Eve"));
    var refusal = Assert.Throws<RelatedRestoreRefusedException>(() => publications.RestoreAsOf(paper, before));
    Assert.Equal(RelatedRestoreRefusal.NoVersionAtMoment, refusal.Reason);
    Assert.Equal(paper, refusal.RecordId);
    Assert.Single(publications.History(paper).Versions);
  }
}
