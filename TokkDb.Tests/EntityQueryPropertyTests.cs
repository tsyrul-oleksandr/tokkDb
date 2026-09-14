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

//NF-5: the five invariants of paging, ordering and relations over generated data and schemas.
//Every case is generated from a seed, so the data and the catalogue of a failing case are a fixed
//snapshot that can be replayed by its seed.
public class EntityQueryPropertyTests {
  private const int Seed = 20260914;
  private const int Cases = 10;

  private readonly ITestOutputHelper _output;

  public EntityQueryPropertyTests(ITestOutputHelper output) {
    _output = output;
  }

  public static TheoryData<int> Seeds() {
    var seeds = new TheoryData<int>();
    for (var i = 0; i < Cases; i++) {
      seeds.Add(Seed + i);
    }
    return seeds;
  }

  //One generated database: a near collection with three orderable columns, one of them often
  //null, and a join column to a far collection; indexes on a random subset of the columns.
  private sealed class Generated : IDisposable {
    public const string Near = "Item";
    public const string Far = "Group";
    public const string Relation = "ItemGroup";
    private static readonly string[] Words = ["alpha", "Beta", "gamma", "Delta", "epsilon", "zeta", "Eta", "theta"];

    public Generated(int seed) {
      Random = new Random(seed);
      File = new TempDatabaseFile();
      Db = new TokkDbConnection(File.Path);
      Db.Load();
      Db.CreateCollection(Far, [new ColumnDescriptor("Code", ValueTypeEnum.Int, unique: true), new ColumnDescriptor("Kind", ValueTypeEnum.String)]);
      Db.CreateCollection(Near, [
        new ColumnDescriptor("A", ValueTypeEnum.Int),
        new ColumnDescriptor("B", ValueTypeEnum.String),
        new ColumnDescriptor("C", ValueTypeEnum.Int),
        new ColumnDescriptor("Ref", ValueTypeEnum.Int)
      ]);
      Db.SetRetentionPolicy(Far, RetentionPolicy.None, dropHistory: true);
      Db.SetRetentionPolicy(Near, RetentionPolicy.None, dropHistory: true);
      Db.CreateRelation(Relation, Near, "Ref", Far, "Code");
      Indexed = new[] { "A", "B", "C" }.Where(_ => Random.Next(2) == 0).ToArray();
      foreach (var column in Indexed) {
        Db.CreateIndex(Near, column);
      }
      var far = Db.Entities(new FieldMapSerializer(), Far);
      var near = Db.Entities(new FieldMapSerializer(), Near);
      var groups = Random.Next(5, 40);
      var items = Random.Next(50, 400);
      Db.InTransaction(() => {
        for (var code = 1; code <= groups; code++) {
          var kind = Words[Random.Next(Words.Length)];
          FarRecords.Add((far.Insert(new Dictionary<string, IDocumentValue> { ["Code"] = new IntDocumentValue(code), ["Kind"] = new StringDocumentValue(kind) }), code, kind));
        }
        for (var i = 0; i < items; i++) {
          var record = new Dictionary<string, IDocumentValue> {
            ["A"] = new IntDocumentValue(Random.Next(-20, 20)),
            ["B"] = new StringDocumentValue(Words[Random.Next(Words.Length)])
          };
          if (Random.Next(3) != 0) {
            record["C"] = new IntDocumentValue(Random.Next(5));
          }
          if (Random.Next(4) != 0) {
            record["Ref"] = new IntDocumentValue(Random.Next(1, groups + 1));
          }
          NearRecords.Add((near.Insert(record), record));
        }
      });
    }

    public Random Random { get; }
    public TempDatabaseFile File { get; }
    public TokkDbConnection Db { get; }
    public string[] Indexed { get; }
    public List<(Ulid Id, int Code, string Kind)> FarRecords { get; } = [];
    public List<(Ulid Id, Dictionary<string, IDocumentValue> Record)> NearRecords { get; } = [];

    public DbEntities<Dictionary<string, IDocumentValue>> Near_ => Db.Entities(new FieldMapSerializer(), Near);

    //A random predicate over the near collection, or none.
    public IExpression Predicate() {
      return Random.Next(5) switch {
        0 => null,
        1 => Compare("A", ComparisonOperator.GreaterOrEqual, Random.Next(-20, 20)),
        2 => CompareText("B", ComparisonOperator.Equal, Words[Random.Next(Words.Length)]),
        3 => In("C", Random.Next(5), Random.Next(5)),
        _ => new AndExpression([Compare("A", ComparisonOperator.Greater, Random.Next(-20, 0)), Compare("A", ComparisonOperator.Less, Random.Next(0, 20))])
      };
    }

    public (string Column, OrderDirection Direction)[] Order() {
      var columns = new[] { "A", "B", "C" }.OrderBy(_ => Random.Next()).Take(Random.Next(1, 3));
      return columns.Select(column => (column, Random.Next(2) == 0 ? OrderDirection.Ascending : OrderDirection.Descending)).ToArray();
    }

    public DbQuery<Dictionary<string, IDocumentValue>> Query(IExpression predicate, (string Column, OrderDirection Direction)[] order) {
      var query = Near_.Query();
      if (predicate is not null) {
        query = query.Where(predicate);
      }
      foreach (var (column, direction) in order) {
        query = query.Equals(Near_.Query()) || query.Build().Order.IsEmpty
          ? direction == OrderDirection.Ascending ? query.OrderBy(column) : query.OrderByDescending(column)
          : direction == OrderDirection.Ascending ? query.ThenBy(column) : query.ThenByDescending(column);
      }
      return query;
    }

    public void DropIndexes() {
      foreach (var column in Indexed) {
        Db.DropIndex(Near, column);
      }
    }

    public void Dispose() {
      Db.Dispose();
      File.Dispose();
    }
  }

  //1 and 2. Pages partition the ordered result, and are duplicate-free and gap-free.
  [Theory]
  [MemberData(nameof(Seeds))]
  public void PagesPartitionTheOrderedResultWithoutDuplicatesOrGaps(int seed) {
    using var data = new Generated(seed);
    var predicate = data.Predicate();
    var order = data.Order();
    var pageSize = data.Random.Next(1, 40);
    var query = data.Query(predicate, order);
    _output.WriteLine($"seed {seed}: {query.Build()}, page {pageSize}, indexed {string.Join(",", data.Indexed)}");

    var whole = query.Run();
    var pages = new List<Ulid>();
    for (var skip = 0L; ; skip += pageSize) {
      var page = query.Skip(skip).Take(pageSize).Run();
      pages.AddRange(page.Records.Select(record => record.RecordId));
      Assert.True(page.Records.Count <= pageSize);
      if (page.Records.Count < pageSize) {
        break;
      }
    }

    Assert.Equal(whole.Records.Select(record => record.RecordId), pages);
    Assert.Equal(pages.Count, pages.Distinct().Count());
    Assert.Equal(whole.Report.RecordsMatched, pages.Count);
  }

  //3. The order is total: over the generated data the comparator is antisymmetric and transitive,
  //and no two distinct records compare equal.
  [Theory]
  [MemberData(nameof(Seeds))]
  public void TheOrderIsTotal(int seed) {
    using var data = new Generated(seed);
    var order = data.Order();
    var query = data.Query(null, order);
    var plan = query.Explain();
    var comparer = new RecordOrder(plan.Order);
    var candidates = data.NearRecords
      .Select(pair => new OrderedCandidate(order.Select(column => KeyEncoder.Encode(pair.Record.GetValueOrDefault(column.Column)).Bytes).ToArray(), pair.Id, default))
      .ToList();
    _output.WriteLine($"seed {seed}: order {string.Join(", ", plan.Order)}, {candidates.Count} records");

    for (var i = 0; i < candidates.Count; i++) {
      Assert.Equal(0, comparer.Compare(candidates[i], candidates[i]));
      for (var j = i + 1; j < candidates.Count; j++) {
        var forward = comparer.Compare(candidates[i], candidates[j]);
        Assert.NotEqual(0, forward);
        Assert.Equal(-Math.Sign(forward), Math.Sign(comparer.Compare(candidates[j], candidates[i])));
      }
    }
    for (var n = 0; n < 500; n++) {
      var a = candidates[data.Random.Next(candidates.Count)];
      var b = candidates[data.Random.Next(candidates.Count)];
      var c = candidates[data.Random.Next(candidates.Count)];
      if (comparer.Compare(a, b) < 0 && comparer.Compare(b, c) < 0) {
        Assert.True(comparer.Compare(a, c) < 0, "the comparison is not transitive");
      }
    }
    //And the engine's sequence is the comparator's.
    candidates.Sort(comparer);
    Assert.Equal(candidates.Select(candidate => candidate.RecordId), query.Run().Records.Select(record => record.RecordId));
  }

  //4. Any is set membership: exactly the near records whose join key is in the projected set, and
  //None exactly the complement.
  [Theory]
  [MemberData(nameof(Seeds))]
  public void AnyIsSetMembershipAndNoneItsComplement(int seed) {
    using var data = new Generated(seed);
    var kind = data.FarRecords[data.Random.Next(data.FarRecords.Count)].Kind;
    var predicate = data.Predicate();
    var inner = CompareText("Kind", ComparisonOperator.Equal, kind);
    _output.WriteLine($"seed {seed}: kind {kind}, predicate {(predicate is null ? "none" : "some")}");

    var any = data.Query(predicate, []).WhereRelated(Generated.Relation, RelationQuantifier.Any, inner).Run();
    var none = data.Query(predicate, []).WhereRelated(Generated.Relation, RelationQuantifier.None, inner).Run();
    var all = data.Query(predicate, []).Run();

    var codes = data.FarRecords.Where(pair => pair.Kind == kind).Select(pair => pair.Code).ToHashSet();
    bool Related(Dictionary<string, IDocumentValue> record) => record.GetValueOrDefault("Ref") is IntDocumentValue reference && codes.Contains(reference.Value);
    var expectedAny = data.NearRecords.Where(pair => Related(pair.Record)).Select(pair => pair.Id).ToHashSet();
    var everything = all.Records.Select(record => record.RecordId).ToHashSet();
    Assert.Equal(everything.Where(expectedAny.Contains).Order(IdentityOrder.Bytes), any.Records.Select(record => record.RecordId).Order(IdentityOrder.Bytes));
    Assert.Equal(everything.Where(id => !expectedAny.Contains(id)).Order(IdentityOrder.Bytes), none.Records.Select(record => record.RecordId).Order(IdentityOrder.Bytes));
    Assert.Equal(everything.Count, any.Records.Count + none.Records.Count);
  }

  //5. The index does not change the answer for a query that asked for an order: the same
  //explicitly ordered query, with and without the indexes, returns identical records in an
  //identical order. For an unordered query the property is over sets and without Take, because
  //Take with no order means any N (PG-4).
  [Theory]
  [MemberData(nameof(Seeds))]
  public void TheIndexDoesNotChangeTheAnswerOfAnOrderedQuery(int seed) {
    using var data = new Generated(seed);
    var predicate = data.Predicate();
    var order = data.Order();
    var skip = data.Random.Next(0, 30);
    var take = data.Random.Next(1, 50);
    var ordered = data.Query(predicate, order).Skip(skip).Take(take);
    var unordered = data.Query(predicate, []);
    _output.WriteLine($"seed {seed}: {ordered.Build()}, indexed {string.Join(",", data.Indexed)}");

    var withIndexes = ordered.Run();
    var setWithIndexes = unordered.Run();
    data.DropIndexes();
    var withoutIndexes = ordered.Run();
    var setWithoutIndexes = unordered.Run();

    _output.WriteLine($"with: {withIndexes.Report}");
    _output.WriteLine($"without: {withoutIndexes.Report}");
    Assert.Equal(withIndexes.Records.Select(record => record.RecordId), withoutIndexes.Records.Select(record => record.RecordId));
    Assert.Equal(setWithIndexes.Records.Select(record => record.RecordId).Order(IdentityOrder.Bytes),
      setWithoutIndexes.Records.Select(record => record.RecordId).Order(IdentityOrder.Bytes));
    Assert.StartsWith("full scan", withoutIndexes.Report.AccessPath);
  }
}
