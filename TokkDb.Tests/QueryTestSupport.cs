using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Records;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//The predicate shapes the entity query tests write, in one place.
internal static class Predicates {
  public static IExpression Column(string name) {
    return new PropertyExpression(name) { Parent = new RootExpression() };
  }

  public static ComparisonExpression Compare(string column, ComparisonOperator op, int value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new IntDocumentValue(value)),
      ValueTypeEnum.Int);
  }

  public static ComparisonExpression CompareText(string column, ComparisonOperator op, string value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new StringDocumentValue(value)),
      ValueTypeEnum.String);
  }

  public static ComparisonExpression CompareDate(string column, ComparisonOperator op, DateTime value) {
    return new ComparisonExpression(Column(column), op, new ConstantExpression(new DateTimeDocumentValue(value)),
      ValueTypeEnum.DateTime);
  }

  public static ComparisonExpression In(string column, params int[] values) {
    return new ComparisonExpression(Column(column), ComparisonOperator.In,
      new ConstantExpression(values.Select(value => (IDocumentValue)new IntDocumentValue(value)).ToList()),
      ValueTypeEnum.Int);
  }

  public static RelationStepExpression Step(string relation, RelationQuantifier quantifier = RelationQuantifier.Any,
      IExpression inner = null) {
    //The resolved names a binder would put here are not what the planner goes by: it resolves the
    //relation from the catalogue, by name.
    return new RelationStepExpression(relation, "unused", "unused", "unused", quantifier, inner);
  }
}

//A record identity in the order of its key bytes, which is the tiebreaker the engine applies
//(OR-1): the bytes of the Ulid, unsigned, which is also its time order.
internal static class IdentityOrder {
  public static readonly IComparer<Ulid> Bytes = Comparer<Ulid>.Create(
    (left, right) => left.ToByteArray().AsSpan().SequenceCompareTo(right.ToByteArray()));
}

public sealed class Event {
  public int Id { get; set; }
  public DateTime Date { get; set; }
  public string City { get; set; }
  public int Cost { get; set; }
  public string Note { get; set; }
}

//NF-1 and the scenarios of §5: one collection of 100 000 records, built once per test collection
//and shared, because building it is the expensive half of every test that needs it. Date has ten
//distinct values, so ten thousand records share each one (S-4); City has forty, so one city is a
//fortieth of the collection and "Nowhere" is no record at all (PG-3a's second row); Cost is not
//indexed and is the column an ordering stage has to sort by.
public sealed class LargeEventsFixture : IDisposable {
  public const string Collection = nameof(Event);
  public const int Count = 100_000;
  public const int DistinctDates = 10;
  public static readonly DateTime FirstDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
  public static readonly string[] Cities = Enumerable.Range(0, 40).Select(i => i == 0 ? "Lviv" : $"City-{i}").ToArray();

  public LargeEventsFixture() {
    File = new TempDatabaseFile();
    Db = new TokkDbConnection(File.Path);
    Db.Load();
    Db.CreateCollection(Collection, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("Date", ValueTypeEnum.DateTime),
      new ColumnDescriptor("City", ValueTypeEnum.String),
      new ColumnDescriptor("Cost", ValueTypeEnum.Int),
      new ColumnDescriptor("Note", ValueTypeEnum.String)
    ]);
    //The data is the fixture; its history is not, and writing one would double the build.
    Db.SetRetentionPolicy(Collection, RetentionPolicy.None, dropHistory: true);
    Db.CreateIndex(Collection, "Date");
    Db.CreateIndex(Collection, "City");
    var random = new Random(20260914);
    var entities = Db.Entities<Event>(Collection);
    Db.InTransaction(() => {
      for (var i = 0; i < Count; i++) {
        var record = new Event {
          Id = i,
          Date = FirstDate.AddDays(i % DistinctDates),
          City = Cities[random.Next(Cities.Length)],
          Cost = random.Next(1000),
          Note = $"event {i}"
        };
        Events.Add(record);
        Ids.Add(entities.Insert(record));
      }
    });
  }

  public TempDatabaseFile File { get; }
  public TokkDbConnection Db { get; }
  public List<Ulid> Ids { get; } = new(Count);
  public List<Event> Events { get; } = new(Count);

  public DbEntities<Event> Entities => Db.Entities<Event>(Collection);

  public IEnumerable<(Ulid Id, Event Event)> Records => Ids.Zip(Events);

  public void Dispose() {
    Db.Dispose();
    File.Dispose();
  }
}

[CollectionDefinition(Name)]
public class LargeEventsCollection : ICollectionFixture<LargeEventsFixture> {
  public const string Name = "Large events";
}
