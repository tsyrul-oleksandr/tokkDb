using TokkDb.Configuration;
using Xunit;

namespace TokkDb.Tests;

public class DatabaseRoundTripTests {
  private static TokkDbConnection NewDatabase(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    return db;
  }

  [Fact]
  public void ANewDatabaseIsEmptyAndReportsItself() {
    using var file = new TempDatabaseFile();
    var db = new TokkDbConnection(file.Path);
    Assert.False(db.IsExists());
    db.CreateDatabase(config => config.CreateEntity<Person>());
    Assert.True(db.IsExists());
    Assert.Empty(db.Entities<Person>().GetAll());
  }

  [Fact]
  public void InsertedRecordsComeBackIntact() {
    using var file = new TempDatabaseFile();
    var entities = NewDatabase(file).Entities<Person>();
    entities.Insert(TestPeople.Ivan());

    var person = Assert.Single(entities.GetAll());
    Assert.Equal(1, person.Id);
    Assert.Equal("Ivan", person.Name);
    Assert.Equal(29, person.Age);
    Assert.Equal("ST-111111", person.Passport.Code);
    Assert.Equal(["tag1", "tag2"], person.Tags.Select(tag => tag.Name));
  }

  [Fact]
  public void RecordsSurviveReopeningTheFile() {
    using var file = new TempDatabaseFile();
    NewDatabase(file).Entities<Person>().Insert(TestPeople.Ivan());

    var reopened = new TokkDbConnection(file.Path);
    Assert.True(reopened.IsExists());
    reopened.Load();

    var person = Assert.Single(reopened.Entities<Person>().GetAll());
    Assert.Equal("Ivan", person.Name);
    Assert.Equal("ST-111111", person.Passport.Code);
  }

  [Theory]
  [InlineData(59)]
  [InlineData(60)]
  [InlineData(500)]
  public void RecordsSpanningManyPagesAllComeBack(int count) {
    using var file = new TempDatabaseFile();
    var entities = NewDatabase(file).Entities<Person>();
    for (var i = 0; i < count; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    var all = entities.GetAll().OrderBy(person => person.Id).ToList();
    Assert.Equal(count, all.Count);
    Assert.Equal(Enumerable.Range(0, count), all.Select(person => person.Id));
    Assert.All(all, person => {
      Assert.Equal($"Person-{person.Id}", person.Name);
      Assert.Equal($"ST-{person.Id:D6}", person.Passport.Code);
      Assert.Equal($"tag-{person.Id}", Assert.Single(person.Tags).Name);
    });
  }

  [Fact]
  public void TheDataPageChainIsFollowedAcrossReopens() {
    using var file = new TempDatabaseFile();
    var entities = NewDatabase(file).Entities<Person>();
    for (var i = 0; i < 500; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    Assert.True(file.PageCount > 2, $"expected the data to span several pages, got {file.PageCount}");

    var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(500, reopened.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void PagesFillUpBeforeANewOneIsAllocated() {
    using var file = new TempDatabaseFile();
    var entities = NewDatabase(file).Entities<Person>();
    for (var i = 0; i < 200; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }

    // 200 records of ~130 bytes must not need more than one page each 8KB of payload.
    var dataPages = file.PageCount - 1;
    Assert.InRange(dataPages, 1, 200 * 200 / TokkConstants.PageSize + 2);
  }
}
