using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

public sealed class Reading {
  public int Id { get; set; }
  public int? Value { get; set; }
}

//Step 2.1: the order model (OR-1, OR-5, OR-8, Q-4). A sequence of column and direction pairs with
//record identity appended in the direction of the last column, applied after the conjuncts and
//the residual, with one comparator object shared by the stage and by anything else that compares
//two positions.
public class OrderModelTests {
  private const string People = nameof(Person);
  private const string Readings = nameof(Reading);

  private readonly ITestOutputHelper _output;

  public OrderModelTests(ITestOutputHelper output) {
    _output = output;
  }

  private static TokkDbConnection NewDatabase(TempDatabaseFile file, bool indexAge) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(People, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Name", ValueTypeEnum.String, unique: true),
      new ColumnDescriptor("Age", ValueTypeEnum.Int),
      new ColumnDescriptor("City", ValueTypeEnum.String),
      new ColumnDescriptor("Passport", ValueTypeEnum.Object),
      new ColumnDescriptor("Tags", ValueTypeEnum.Array)
    ]);
    if (indexAge) {
      db.CreateIndex(People, "Age");
    }
    return db;
  }

  private static List<Ulid> Fill(TokkDbConnection db, int count) {
    var entities = db.Entities<Person>(People);
    var ids = new List<Ulid>(count);
    db.InTransaction(() => {
      for (var i = 0; i < count; i++) {
        ids.Add(entities.Insert(TestPeople.Numbered(i)));
      }
    });
    return ids;
  }

  //Numbered() gives ages 20 + i % 40, so ten records of four hundred share each age.
  private static IEnumerable<(Ulid Id, int Age)> ByAge(List<Ulid> ids) {
    return ids.Select((id, i) => (id, 20 + i % 40));
  }

  //Ties are broken by identity, in the key order of the identity's bytes, whether the order came
  //from an index walk or from the ordering stage — so the same query gives the same sequence on
  //every run, and the two sources give the same sequence as each other. The page is bounded
  //because that is when the planner walks the index for the order (OR-2a); a bound of the whole
  //collection changes nothing else.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void RecordsEqualOnEveryDeclaredColumnKeepTheSameRelativePositionAcrossRuns(bool indexed) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, indexed);
    var ids = Fill(db, 400);
    var query = db.Entities<Person>(People).Query().OrderBy("Age").Take(400);

    var runs = Enumerable.Range(0, 3).Select(_ => query.Run()).ToList();

    _output.WriteLine(runs[0].Report.ToString());
    Assert.Equal(indexed ? OrderSource.IndexWalk : OrderSource.BoundedHeap, runs[0].Report.OrderSource);
    var expected = ByAge(ids).OrderBy(pair => pair.Age).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Select(pair => pair.Id);
    foreach (var run in runs) {
      Assert.Equal(expected, run.Records.Select(record => record.RecordId));
    }
  }

  //OR-1: the tiebreaker runs in the direction of the last declared column, which is the direction
  //the composite (value, identity) key of an index is already in.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void ADescendingLastColumnGivesDescendingIdentityOrder(bool indexed) {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, indexed);
    var ids = Fill(db, 400);
    var entities = db.Entities<Person>(People);

    var descending = entities.Query().OrderByDescending("Age").Run();
    //Two columns: the identity follows the direction of the last one, not the first.
    var mixed = entities.Query().OrderBy("City").ThenByDescending("Age").Run();

    Assert.Equal(ByAge(ids).OrderByDescending(pair => pair.Age).ThenByDescending(pair => pair.Id, IdentityOrder.Bytes)
      .Select(pair => pair.Id), descending.Records.Select(record => record.RecordId));
    //City is missing from every record, so the order is Age descending and then identity descending.
    Assert.Equal(descending.Records.Select(record => record.RecordId), mixed.Records.Select(record => record.RecordId));
    Assert.NotEqual(OrderSource.IndexWalk, descending.Report.OrderSource);
  }

  //OR-5: the order is applied to the matches and to nothing else. A query keeping five records out
  //of many thousands sorts five, which the ordering stage's retained count shows.
  [Fact]
  public void AQueryFilteringToFiveRecordsOrdersFive() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, indexAge: false);
    Fill(db, 20_000);

    var result = db.Entities<Person>(People).Query()
      .Where(In("Id", 17, 4_000, 8_888, 12_345, 19_999))
      .OrderBy("City").ThenByDescending("Age")
      .Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(20_000, result.Report.RecordsExamined);
    Assert.Equal(5, result.Report.RecordsMatched);
    Assert.Equal(5, result.Report.RecordsRetained);
    Assert.Equal(5, result.Report.DocumentsMaterialised);
    Assert.Equal(OrderSource.CompleteSort, result.Report.OrderSource);
    //City is missing from every record; the ages are 59, 45, 37, 28 and 20.
    Assert.Equal([19_999, 12_345, 17, 8_888, 4_000], result.Records.Select(record => record.Value.Id));
  }

  //OR-8: a null sorts before every value ascending and after every value descending, and it lands
  //in the same place whether the order came from a walk or from the stage — because both order by
  //the encoded key, and the null key sorts below every other.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void NullsSortFirstAscendingAndLastDescendingFromAWalkAndFromTheStageAlike(bool indexed) {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection(Readings, [new ColumnDescriptor("Id", ValueTypeEnum.Int), new ColumnDescriptor("Value", ValueTypeEnum.Int)]);
    if (indexed) {
      db.CreateIndex(Readings, "Value");
    }
    var entities = db.Entities<Reading>(Readings);
    var ids = new List<Ulid>();
    db.InTransaction(() => {
      for (var i = 0; i < 30; i++) {
        ids.Add(entities.Insert(new Reading { Id = i, Value = i % 3 == 0 ? null : 100 - i }));
      }
    });
    var nulls = ids.Where((_, i) => i % 3 == 0).Order(IdentityOrder.Bytes).ToList();
    var values = ids.Select((id, i) => (id, value: 100 - i)).Where((_, i) => i % 3 != 0).ToList();

    var ascending = entities.Query().OrderBy("Value").Take(30).Run();
    var descending = entities.Query().OrderByDescending("Value").Take(30).Run();

    _output.WriteLine(ascending.Report.ToString());
    Assert.Equal(indexed ? OrderSource.IndexWalk : OrderSource.BoundedHeap, ascending.Report.OrderSource);
    Assert.Equal(OrderSource.BoundedHeap, descending.Report.OrderSource);
    Assert.Equal([.. nulls, .. values.OrderBy(pair => pair.value).Select(pair => pair.id)],
      ascending.Records.Select(record => record.RecordId));
    Assert.Equal([.. values.OrderByDescending(pair => pair.value).Select(pair => pair.id), .. nulls.AsEnumerable().Reverse()],
      descending.Records.Select(record => record.RecordId));
  }

  //One comparator object. The engine's sequence is what RecordOrder says it is: sorting the same
  //records by the same comparator over their encoded keys gives the same sequence, and every
  //adjacent pair compares strictly, so the order is total.
  [Fact]
  public void TheComparatorTheStageUsesIsTheOneAnythingElseComparesTwoPositionsWith() {
    using var file = new TempDatabaseFile();
    using var db = NewDatabase(file, indexAge: true);
    Fill(db, 200);
    var query = db.Entities<Person>(People).Query().OrderBy("Age").ThenByDescending("Name");

    var plan = query.Explain();
    var result = query.Run();
    var order = new RecordOrder(plan.Order);
    var candidates = result.Records
      .Select(record => new OrderedCandidate([
        KeyEncoder.Encode(new IntDocumentValue(record.Value.Age)).Bytes,
        KeyEncoder.Encode(new StringDocumentValue(record.Value.Name)).Bytes
      ], record.RecordId, default))
      .ToList();

    var shuffled = candidates.OrderBy(candidate => candidate.RecordId.ToString().GetHashCode()).ToList();
    shuffled.Sort(order);
    Assert.Equal(candidates.Select(candidate => candidate.RecordId), shuffled.Select(candidate => candidate.RecordId));
    for (var i = 1; i < candidates.Count; i++) {
      Assert.True(order.Compare(candidates[i - 1], candidates[i]) < 0, $"positions {i - 1} and {i} do not compare strictly");
      Assert.True(order.Compare(candidates[i], candidates[i - 1]) > 0, "the comparison is not antisymmetric");
    }
  }
}
