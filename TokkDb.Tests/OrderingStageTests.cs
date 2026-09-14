using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Query;
using TokkDb.Pages.Records;
using TokkDb.Values;
using Xunit;
using Xunit.Abstractions;
using static TokkDb.Tests.Predicates;

namespace TokkDb.Tests;

//Step 2.3: the ordering stage (OR-3, OR-3a, OR-3b, OR-4, OR-6, OR-6a, OR-7, Q-12). Over keys and
//addresses, never documents; at most Skip + Take held for a bounded page; every candidate still
//examined, so not streaming; the cross-type order as a contract; and the comparator over the
//encoded key order and nothing else.
[Collection(LargeEventsCollection.Name)]
public class OrderingStageTests {
  private readonly LargeEventsFixture _events;
  private readonly ITestOutputHelper _output;

  public OrderingStageTests(LargeEventsFixture events, ITestOutputHelper output) {
    _events = events;
    _output = output;
  }

  // =====================================================================
  // The bounded heap over a hundred thousand records (OR-3, OR-3a, Q-12).
  // =====================================================================

  [Fact]
  public void OrderingAHundredThousandRecordsByAnUnindexedColumnAndTakingTwentyRetainsTwentyAndExaminesAll() {
    var result = _events.Entities.Query().OrderBy("Cost").Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    Assert.Equal(LargeEventsFixture.Count, result.Report.RecordsExamined);
    Assert.Equal(LargeEventsFixture.Count, result.Report.RecordsMatched);
    Assert.Equal(20, result.Report.RecordsRetained);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
    Assert.Equal(20, result.Report.RecordsReturned);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.False(result.Report.IsStreaming);
    Assert.Contains("not streaming", result.Report.ToString());
    var expected = _events.Records.OrderBy(pair => pair.Event.Cost).ThenBy(pair => pair.Id, IdentityOrder.Bytes).Take(20);
    Assert.Equal(expected.Select(pair => pair.Id), result.Records.Select(record => record.RecordId));
  }

  //PG-5: the stage is bounded by Skip + Take, not by the match count.
  [Fact]
  public void TheStageRetainsSkipPlusTakeRecordsAndNotTheMatchCount() {
    var result = _events.Entities.Query()
      .Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Skip(30).Take(20).Run();

    _output.WriteLine(result.Report.ToString());
    var lviv = _events.Events.Count(record => record.City == "Lviv");
    Assert.Equal(lviv, result.Report.RecordsMatched);
    Assert.Equal(50, result.Report.RecordsRetained);
    Assert.Equal(30, result.Report.RecordsSkipped);
    Assert.Equal(20, result.Report.RecordsReturned);
    Assert.Equal(20, result.Report.DocumentsMaterialised);
  }

  //OR-3b: with no Take the stage holds every match, and says so.
  [Fact]
  public void WithNoTakeTheStageHoldsEveryMatchAndReportsACompleteSort() {
    var result = _events.Entities.Query()
      .Where(CompareText("City", ComparisonOperator.Equal, "Lviv"))
      .OrderBy("Cost").Run();

    _output.WriteLine(result.Report.ToString());
    var lviv = _events.Events.Count(record => record.City == "Lviv");
    Assert.Equal(OrderSource.CompleteSort, result.Report.OrderSource);
    Assert.Equal(lviv, result.Report.RecordsRetained);
    Assert.Equal(lviv, result.Report.DocumentsMaterialised);
    Assert.Contains("complete sort", result.Report.ToString());
  }

  //OR-7 and N-6a: a complete sort over more records than the cap allows fails before a page is
  //returned, naming the stage, the size it reached and the cap.
  [Fact]
  public void ACompleteSortOverTheCapFailsWithItsThreeNumbers() {
    var reports = new List<QueryReport>();
    _events.Db.Queries.QueryExecuted += reports.Add;
    try {
      var query = _events.Entities.Query().OrderBy("Cost")
        .WithOptions(new QueryOptions { OrderingStageCapBytes = 50_000 });

      var failed = Assert.Throws<QueryCapExceededException>(() => query.Run());

      _output.WriteLine(failed.Message);
      Assert.Equal(QueryCap.OrderingStage, failed.Cap);
      Assert.Equal(LargeEventsFixture.Collection, failed.Subject);
      Assert.True(failed.SizeReached > failed.Limit);
      Assert.Equal(50_000, failed.Limit);
      Assert.Contains("ordering stage", failed.Message);
      Assert.Contains(failed.SizeReached.ToString(), failed.Message);
      Assert.Contains("50000 bytes", failed.Message);
      //DG-3a: one event, with the figures reached when it failed, and well short of the collection.
      var report = Assert.Single(reports);
      Assert.Equal(QueryOutcome.Failed, report.Outcome);
      Assert.True(report.RecordsExamined > 0 && report.RecordsExamined < LargeEventsFixture.Count);
      Assert.Equal(0, report.DocumentsMaterialised);
    } finally {
      _events.Db.Queries.QueryExecuted -= reports.Add;
    }
  }

  // =====================================================================
  // Descending (OR-4, Q-3, R-1).
  // =====================================================================

  //Recorded here so that the day a backward leaf chain exists, the test that has to change is
  //this one: today BPlusTree.Range descends once and follows NextPageIndex from leaf to leaf, and
  //there is no previous pointer, so a descending order over an indexed column is a sort.
  [Fact]
  public void DescendingOverAnIndexedColumnIsSortedBecauseTheLeafChainRunsForwardOnly() {
    var query = _events.Entities.Query().OrderByDescending("Date").Take(20);

    var plan = query.Explain();
    var result = query.Run();

    _output.WriteLine(plan.ToString());
    _output.WriteLine(result.Report.ToString());
    Assert.Equal(OrderSource.BoundedHeap, plan.OrderSource);
    Assert.Contains("walked forward only", plan.OrderReason);
    Assert.Contains("no backward chain", plan.OrderReason);
    Assert.Equal(OrderSource.BoundedHeap, result.Report.OrderSource);
    Assert.Equal(LargeEventsFixture.Count, result.Report.RecordsExamined);
    var expected = _events.Records.OrderByDescending(pair => pair.Event.Date).ThenByDescending(pair => pair.Id, IdentityOrder.Bytes).Take(20);
    Assert.Equal(expected.Select(pair => pair.Id), result.Records.Select(record => record.RecordId));
  }

  // =====================================================================
  // Keys and addresses, never documents (OR-3).
  // =====================================================================

  public sealed class Narrow {
    public int Cost { get; set; }
  }

  public sealed class Wide {
    public int Cost { get; set; }
    public string Padding { get; set; }
  }

  //The stage's memory is a function of the order keys and not of the record: two collections with
  //the same keys and records of very different widths account the same bytes.
  [Fact]
  public void TheStageMemoryDoesNotGrowWithTheWidthOfTheRecord() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    db.CreateCollection("Narrow", [new ColumnDescriptor("Cost", ValueTypeEnum.Int)]);
    db.CreateCollection("Wide", [new ColumnDescriptor("Cost", ValueTypeEnum.Int), new ColumnDescriptor("Padding", ValueTypeEnum.String)]);
    var narrow = db.Entities<Narrow>("Narrow");
    var wide = db.Entities<Wide>("Wide");
    db.InTransaction(() => {
      for (var i = 0; i < 300; i++) {
        narrow.Insert(new Narrow { Cost = (i * 37) % 100 });
        wide.Insert(new Wide { Cost = (i * 37) % 100, Padding = new string('x', 2_000) });
      }
    });

    var narrowReport = narrow.Query().OrderBy("Cost").Take(50).Run().Report;
    var wideReport = wide.Query().OrderBy("Cost").Take(50).Run().Report;

    _output.WriteLine(narrowReport.ToString());
    _output.WriteLine(wideReport.ToString());
    Assert.Equal(50, narrowReport.RecordsRetained);
    Assert.Equal(narrowReport.OrderingStageBytes, wideReport.OrderingStageBytes);
    Assert.True(narrowReport.OrderingStageBytes < 50 * 200, "the stage accounts keys and addresses, not records");
  }

  // =====================================================================
  // The cross-type order is the contract (OR-6, OR-6a).
  // =====================================================================

  private const string Typed = "Typed";

  //Every scalar the document format can hold, one representative value each, in the order the key
  //encoding puts them: null, boolean, signed integer (Int and Long share a tag), unsigned integer,
  //decimal, date and time, guid, ulid, text. Floating point and time span have a place in the tag
  //order (KeyTag) but no document value to store them by, so they cannot reach a column.
  public static readonly (string Type, IDocumentValue Value)[] TagOrder = [
    ("null", new NullDocumentValue()),
    ("boolean", new BooleanDocumentValue(true)),
    ("int", new IntDocumentValue(int.MaxValue)),
    ("long", new LongDocumentValue(long.MaxValue)),
    ("uint", new UIntDocumentValue(0)),
    ("decimal", new DecimalDocumentValue(-1000m)),
    ("datetime", new DateTimeDocumentValue(DateTime.MinValue)),
    ("guid", new GuidDocumentValue(Guid.Empty)),
    ("ulid", new UlidDocumentValue(Ulid.MinValue)),
    ("string", new StringDocumentValue(""))
  ];

  public static TheoryData<string, string> TypePairs() {
    var pairs = new TheoryData<string, string>();
    for (var i = 0; i < TagOrder.Length; i++) {
      for (var j = i + 1; j < TagOrder.Length; j++) {
        pairs.Add(TagOrder[i].Type, TagOrder[j].Type);
      }
    }
    return pairs;
  }

  private static DbEntities<Dictionary<string, IDocumentValue>> TypedEntities(TokkDbConnection db, ValueTypeEnum? declared) {
    db.CreateCollection(Typed, declared is { } type ? [new ColumnDescriptor("Value", type)] : []);
    db.SetRetentionPolicy(Typed, RetentionPolicy.None, dropHistory: true);
    return db.Entities(new FieldMapSerializer(), Typed);
  }

  private static Dictionary<string, IDocumentValue> Record(IDocumentValue value) {
    return value is NullDocumentValue ? [] : new Dictionary<string, IDocumentValue> { ["Value"] = value };
  }

  //Every pair of types: the one with the lower tag comes first, whatever the values, and however
  //they were inserted.
  [Theory]
  [MemberData(nameof(TypePairs))]
  public void ValuesOfTwoTypesOrderByTheirTagsAndNotByTheirValues(string lower, string higher) {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var entities = TypedEntities(db, declared: null);
    var first = TagOrder.Single(pair => pair.Type == lower).Value;
    var second = TagOrder.Single(pair => pair.Type == higher).Value;
    Ulid firstId = default, secondId = default;
    //Inserted the other way round, so that insertion order cannot pass for key order.
    db.InTransaction(() => {
      secondId = entities.Insert(Record(second));
      firstId = entities.Insert(Record(first));
    });

    var ascending = entities.Query().OrderBy("Value").Run();
    var descending = entities.Query().OrderByDescending("Value").Run();

    Assert.Equal([firstId, secondId], ascending.Records.Select(record => record.RecordId));
    Assert.Equal([secondId, firstId], descending.Records.Select(record => record.RecordId));
    //A null is not a second type, and an Int and a Long are one type to the encoding (one tag,
    //one width), which is what lets a widened column keep its index.
    var oneTag = lower == "null" || (lower == "int" && higher == "long");
    Assert.Equal(oneTag ? [] : ["Value"], ascending.Report.MixedTypeColumns);
  }

  //The whole contract at once, and the two edges that surprise people: signed and unsigned
  //integers sort in separate runs however close their numbers are, and 5 as an integer is not the
  //same key as 5.0 — an Int 6 sorts before a Decimal 5.0, because the tag decides first.
  [Fact]
  public void AMixedTypeColumnOrdersByTheTagOrderDeterministicallyAndSaysSo() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var entities = TypedEntities(db, declared: null);
    var values = new List<(string Label, IDocumentValue Value)> {
      ("string b", new StringDocumentValue("b")),
      ("uint 5", new UIntDocumentValue(5)),
      ("decimal 5.0", new DecimalDocumentValue(5.0m)),
      ("int 10", new IntDocumentValue(10)),
      ("null", new NullDocumentValue()),
      ("int 6", new IntDocumentValue(6)),
      ("string a", new StringDocumentValue("a")),
      ("guid", new GuidDocumentValue(Guid.Empty)),
      ("bool", new BooleanDocumentValue(false)),
      ("ulid", new UlidDocumentValue(Ulid.MinValue)),
      ("long 7", new LongDocumentValue(7)),
      ("datetime", new DateTimeDocumentValue(new DateTime(2024, 1, 1)))
    };
    var ids = new Dictionary<Ulid, string>();
    db.InTransaction(() => {
      foreach (var (label, value) in values) {
        ids[entities.Insert(Record(value))] = label;
      }
    });

    var results = Enumerable.Range(0, 3).Select(_ => entities.Query().OrderBy("Value").Run()).ToList();

    _output.WriteLine(results[0].Report.ToString());
    var labels = results[0].Records.Select(record => ids[record.RecordId]).ToList();
    Assert.Equal(["null", "bool", "int 6", "long 7", "int 10", "uint 5", "decimal 5.0", "datetime", "guid", "ulid", "string a", "string b"], labels);
    Assert.All(results, result => Assert.Equal(labels, result.Records.Select(record => ids[record.RecordId])));
    Assert.Equal(["Value"], results[0].Report.MixedTypeColumns);
    Assert.Contains("mixed types in Value", results[0].Report.ToString());
  }

  // =====================================================================
  // The stage and an index walk agree on every supported type (OR-6a).
  // =====================================================================

  public static TheoryData<string> SupportedTypes() {
    var types = new TheoryData<string>();
    foreach (var type in new[] { "boolean", "int", "uint", "long", "decimal", "datetime", "guid", "ulid", "string" }) {
      types.Add(type);
    }
    return types;
  }

  //Edge values per type, the ones the key encoder was written to get right included, with
  //repeats so that ties reach the identity tiebreaker. Some records carry no value at all.
  private static (ValueTypeEnum Declared, IDocumentValue[] Values) EdgeValues(string type) {
    var random = new Random(7);
    return type switch {
      "boolean" => (ValueTypeEnum.Boolean, [new BooleanDocumentValue(true), new BooleanDocumentValue(false), new BooleanDocumentValue(true)]),
      "int" => (ValueTypeEnum.Int, [new IntDocumentValue(int.MinValue), new IntDocumentValue(-1), new IntDocumentValue(0), new IntDocumentValue(1),
        new IntDocumentValue(int.MaxValue), new IntDocumentValue(7), new IntDocumentValue(7), new IntDocumentValue(-7)]),
      "uint" => (ValueTypeEnum.UInt, [new UIntDocumentValue(uint.MaxValue), new UIntDocumentValue(0), new UIntDocumentValue(1),
        new UIntDocumentValue(7), new UIntDocumentValue(7), new UIntDocumentValue(int.MaxValue + 1u)]),
      "long" => (ValueTypeEnum.Long, [new LongDocumentValue(long.MinValue), new LongDocumentValue(-1), new LongDocumentValue(0),
        new LongDocumentValue(long.MaxValue), new LongDocumentValue(7), new LongDocumentValue(7), new LongDocumentValue(1L << 40)]),
      //1.5 and 1.50 are one value at two scales, and one key.
      "decimal" => (ValueTypeEnum.Decimal, [new DecimalDocumentValue(-1.5m), new DecimalDocumentValue(-1.50m), new DecimalDocumentValue(0m),
        new DecimalDocumentValue(1.5m), new DecimalDocumentValue(1.50m), new DecimalDocumentValue(0.001m), new DecimalDocumentValue(1000m),
        new DecimalDocumentValue(decimal.MinValue), new DecimalDocumentValue(decimal.MaxValue), new DecimalDocumentValue(123.456m),
        new DecimalDocumentValue(-0.0001m)]),
      "datetime" => (ValueTypeEnum.DateTime, [new DateTimeDocumentValue(DateTime.MinValue), new DateTimeDocumentValue(DateTime.MaxValue),
        new DateTimeDocumentValue(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), new DateTimeDocumentValue(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local)),
        new DateTimeDocumentValue(new DateTime(1999, 12, 31)), new DateTimeDocumentValue(DateTime.UnixEpoch)]),
      "guid" => (ValueTypeEnum.Guid, [
        .. Enumerable.Range(0, 12).Select(_ => (IDocumentValue)new GuidDocumentValue(new Guid(RandomBytes(random, 16)))),
        new GuidDocumentValue(Guid.Empty), new GuidDocumentValue(new Guid("00000100-0000-0000-0000-000000000000")),
        new GuidDocumentValue(new Guid("00010000-0000-0000-0000-000000000000")), new GuidDocumentValue(new Guid("80000000-0000-0000-0000-000000000000"))]),
      "ulid" => (ValueTypeEnum.Ulid, [.. Enumerable.Range(0, 8).Select(_ => (IDocumentValue)new UlidDocumentValue(Ulid.NewUlid())),
        new UlidDocumentValue(Ulid.MinValue), new UlidDocumentValue(Ulid.MaxValue)]),
      //Folded and, past 128 characters, truncated: two long strings with one prefix are one key.
      "string" => (ValueTypeEnum.String, [new StringDocumentValue(""), new StringDocumentValue("a"), new StringDocumentValue("A"),
        new StringDocumentValue("b"), new StringDocumentValue("B"), new StringDocumentValue("ä"), new StringDocumentValue("Z"),
        new StringDocumentValue("z"), new StringDocumentValue("aa"), new StringDocumentValue("Ab"), new StringDocumentValue("Олена"),
        new StringDocumentValue("олена"), new StringDocumentValue(new string('x', 300)), new StringDocumentValue(new string('x', 300) + "y"),
        new StringDocumentValue("x\0y")]),
      _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
  }

  private static byte[] RandomBytes(Random random, int count) {
    var bytes = new byte[count];
    random.NextBytes(bytes);
    return bytes;
  }

  //The same data ordered through an index walk, ascending, and through the ordering stage after
  //the index is dropped, gives one sequence; descending is the exact reverse of it, because the
  //identity tiebreaker turns with the last column (OR-1).
  [Theory]
  [MemberData(nameof(SupportedTypes))]
  public void TheStageAndAnIndexWalkAgreeOnEverySupportedType(string type) {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var (declared, values) = EdgeValues(type);
    var entities = TypedEntities(db, declared);
    db.CreateIndex(Typed, "Value");
    var count = 0;
    db.InTransaction(() => {
      foreach (var value in values) {
        entities.Insert(Record(value));
        entities.Insert(Record(value));
        count += 2;
      }
      entities.Insert(Record(new NullDocumentValue()));
      entities.Insert(Record(new NullDocumentValue()));
      count += 2;
    });

    var walk = entities.Query().OrderBy("Value").Take(count).Run();
    var descendingIndexed = entities.Query().OrderByDescending("Value").Take(count).Run();
    db.DropIndex(Typed, "Value");
    var stage = entities.Query().OrderBy("Value").Take(count).Run();
    var descending = entities.Query().OrderByDescending("Value").Take(count).Run();

    _output.WriteLine(walk.Report.ToString());
    _output.WriteLine(stage.Report.ToString());
    Assert.Equal(OrderSource.IndexWalk, walk.Report.OrderSource);
    Assert.Equal(OrderSource.BoundedHeap, stage.Report.OrderSource);
    Assert.Equal(count, walk.Records.Count);
    Assert.Equal(walk.Records.Select(record => record.RecordId), stage.Records.Select(record => record.RecordId));
    Assert.Equal(walk.Records.Select(record => record.RecordId).Reverse(), descendingIndexed.Records.Select(record => record.RecordId));
    Assert.Equal(walk.Records.Select(record => record.RecordId).Reverse(), descending.Records.Select(record => record.RecordId));
    Assert.Empty(walk.Report.MixedTypeColumns);
  }

  // =====================================================================
  // The comparator is the encoded key order and never a CLR comparison (OR-6a).
  // =====================================================================

  private static OrderedCandidate Candidate(EncodedKey key, Ulid id) {
    return new OrderedCandidate([key.Bytes], id, default);
  }

  //The four values the key encoder exists to get right, compared through the one comparator over
  //their keys: negative zero and zero are one key, NaN sorts below negative infinity, decimals
  //equal at different scales are one key, and a Guid orders by the bytes of its key — which is
  //Guid.CompareTo's order and not the order of ToByteArray(), whose first three fields are
  //little-endian. A comparator that reached for the CLR's byte order would agree everywhere else
  //and disagree exactly here.
  [Fact]
  public void TheComparatorOrdersTheEdgesByTheirKeysAndNotByACLRComparison() {
    var order = new RecordOrder([new OrderColumn("Value")]);
    var first = Ulid.MinValue;
    var second = Ulid.MaxValue;

    //Ties on the value fall through to the identity, so a tie is exactly "the identity decides".
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(-0.0), second), Candidate(KeyEncoder.Encode(0.0), first)) > 0);
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(-0.0), first), Candidate(KeyEncoder.Encode(0.0), second)) < 0);
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(double.NaN), second), Candidate(KeyEncoder.Encode(double.NegativeInfinity), first)) < 0);
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(1.5m), second), Candidate(KeyEncoder.Encode(1.50m), first)) > 0);
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(1.5m), first), Candidate(KeyEncoder.Encode(1.50m), second)) < 0);

    var low = new Guid("00000100-0000-0000-0000-000000000000");
    var high = new Guid("00010000-0000-0000-0000-000000000000");
    Assert.True(order.Compare(Candidate(KeyEncoder.Encode(low), second), Candidate(KeyEncoder.Encode(high), first)) < 0);
    Assert.True(low.CompareTo(high) < 0, "the key order is CompareTo's order");
    Assert.True(low.ToByteArray().AsSpan().SequenceCompareTo(high.ToByteArray()) > 0,
      "the CLR's byte layout puts them the other way round, which is the comparison the stage must never fall back on");
  }

  //Stored and read back through the engine: the Guid pair above orders as its keys say.
  [Fact]
  public void GuidsOrderByTheirKeysThroughTheStageAndTheWalkAlike() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.Load();
    var entities = TypedEntities(db, ValueTypeEnum.Guid);
    db.CreateIndex(Typed, "Value");
    var guids = new[] {
      new Guid("00010000-0000-0000-0000-000000000000"),
      new Guid("00000100-0000-0000-0000-000000000000"),
      new Guid("80000000-0000-0000-0000-000000000000"),
      new Guid("00000000-0000-0000-0000-000000000001")
    };
    var ids = new Dictionary<Ulid, Guid>();
    db.InTransaction(() => {
      foreach (var guid in guids) {
        ids[entities.Insert(Record(new GuidDocumentValue(guid)))] = guid;
      }
    });

    var walk = entities.Query().OrderBy("Value").Take(10).Run();
    db.DropIndex(Typed, "Value");
    var stage = entities.Query().OrderBy("Value").Take(10).Run();

    var expected = guids.Order().ToList();
    Assert.Equal(expected, walk.Records.Select(record => ids[record.RecordId]));
    Assert.Equal(expected, stage.Records.Select(record => ids[record.RecordId]));
    Assert.NotEqual(expected, guids.OrderBy(guid => guid.ToByteArray(), Comparer<byte[]>.Create((l, r) => l.AsSpan().SequenceCompareTo(r))));
  }
}
