using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.Benchmarks.Benchmarks;

//NF-2 and NF-3: the operation sequences the versioning numbers are measured on. Each workload
//is a column set, a number of records, and a deterministic sequence of writes — (record,
//document) pairs — so that every configuration of k and the ratio sees exactly the same
//history and the numbers compare.
public sealed class VersioningWorkload {
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required List<ColumnDescriptor> Columns { get; init; }
  public required int Records { get; init; }
  public required List<(int Record, Dictionary<string, IDocumentValue> Document)> Steps { get; init; }
  public string ElementKeyColumn { get; init; } = string.Empty;
  public string ElementKey { get; init; } = string.Empty;

  public int Versions => Steps.Count;

  private static Dictionary<string, IDocumentValue> Map(params (string Name, IDocumentValue Value)[] fields) {
    return fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
  }

  private static Dictionary<string, IDocumentValue> Copy(Dictionary<string, IDocumentValue> document) {
    return new Dictionary<string, IDocumentValue>(document, StringComparer.Ordinal);
  }

  //NF-2's first: a record of 50 fields, one field changed per version.
  public static VersioningWorkload SmallEdits(int versions) {
    return FieldEdits("Small edits, 50 fields", "one of 50 fields changed per version", 50, versions, changedPerVersion: 1);
  }

  //NF-3's fourth: a wide record of 200 fields, one field changed per version.
  public static VersioningWorkload WideRecordEdits(int versions) {
    return FieldEdits("Wide record edits, 200 fields", "one of 200 fields changed per version", 200, versions, changedPerVersion: 1);
  }

  //A column declaration costs the catalogue about 170 bytes, and a collection's descriptor has
  //to fit a page, so a collection cannot declare more than about 45 columns today. The
  //records of these workloads carry every field; the schema declares the first twelve, which
  //is enough for the engine to read and index them and does not change what a delta costs.
  private const int DeclaredColumns = 12;

  //NF-2's third: every field changed on every version.
  public static VersioningWorkload CompleteRewrites(int versions) {
    return FieldEdits("Complete rewrites, 50 fields", "every one of 50 fields changed per version", 50, versions, changedPerVersion: 50);
  }

  private static VersioningWorkload FieldEdits(string name, string description, int fields, int versions, int changedPerVersion) {
    var random = new Random(fields * 1000 + changedPerVersion);
    var columns = Enumerable.Range(0, Math.Min(fields, DeclaredColumns))
      .Select(i => new ColumnDescriptor($"field{i:D3}", i % 3 == 0 ? ValueTypeEnum.String : ValueTypeEnum.Int)).ToList();
    var document = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    for (var i = 0; i < fields; i++) {
      document[$"field{i:D3}"] = i % 3 == 0 ? new StringDocumentValue($"value {i} of record") : new IntDocumentValue(i * 7);
    }
    var steps = new List<(int, Dictionary<string, IDocumentValue>)> { (0, Copy(document)) };
    for (var version = 1; version < versions; version++) {
      var next = Copy(document);
      foreach (var i in Enumerable.Range(0, fields).OrderBy(_ => random.Next()).Take(changedPerVersion)) {
        next[$"field{i:D3}"] = i % 3 == 0
          ? new StringDocumentValue($"value {i} at version {version}")
          : new IntDocumentValue(i * 7 + version);
      }
      document = next;
      steps.Add((0, Copy(document)));
    }
    return new VersioningWorkload { Name = name, Description = description, Columns = columns, Records = 1, Steps = steps };
  }

  //NF-2's second: an array that gains one element per version.
  public static VersioningWorkload ArrayAppends(int versions) {
    var columns = new List<ColumnDescriptor> {
      new("title", ValueTypeEnum.String), new("items", ValueTypeEnum.Array)
    };
    var items = new List<IDocumentValue>();
    var steps = new List<(int, Dictionary<string, IDocumentValue>)>();
    for (var version = 0; version < versions; version++) {
      items.Add(new ObjectDocumentValue(Map(("id", new IntDocumentValue(version)), ("text", new StringDocumentValue($"item {version} appended")))));
      steps.Add((0, Map(("title", new StringDocumentValue("a list")), ("items", new ArrayDocumentValue(items.ToArray())))));
    }
    return new VersioningWorkload {
      Name = "Array appends", Description = "one element appended to an array per version",
      Columns = columns, Records = 1, Steps = steps
    };
  }

  //NF-3's second: the benchmark project's Publication, with its nested author and keyword
  //arrays, edited the way a bibliography is: titles corrected, keywords added, authors
  //reordered, now and then a record rewritten.
  public static VersioningWorkload Publications(int versions) {
    var random = new Random(7);
    var columns = new List<ColumnDescriptor> {
      new("Title", ValueTypeEnum.String), new("Doi", ValueTypeEnum.String), new("Year", ValueTypeEnum.Int),
      new("Institution", ValueTypeEnum.String), new("DocumentType", ValueTypeEnum.String),
      new("Authors", ValueTypeEnum.Array, elementKey: "id"), new("Keywords", ValueTypeEnum.Array)
    };
    ObjectDocumentValue Author(int id) => new(Map(("id", new IntDocumentValue(id)), ("Name", new StringDocumentValue($"Author {id}"))));
    ObjectDocumentValue Keyword(int i) => new(Map(("Name", new StringDocumentValue($"keyword-{i}"))));
    var authors = new List<IDocumentValue> { Author(1), Author(2), Author(3) };
    var keywords = new List<IDocumentValue> { Keyword(1), Keyword(2) };
    var title = "Publication 1";
    var year = 2020;
    Dictionary<string, IDocumentValue> Current() => Map(
      ("Title", new StringDocumentValue(title)), ("Doi", new StringDocumentValue("10.1000/tokkdb.00000001")),
      ("Year", new IntDocumentValue(year)), ("Institution", new StringDocumentValue("Institution 7")),
      ("DocumentType", new StringDocumentValue("article")),
      ("Authors", new ArrayDocumentValue(authors.ToArray())), ("Keywords", new ArrayDocumentValue(keywords.ToArray())));
    var steps = new List<(int, Dictionary<string, IDocumentValue>)> { (0, Current()) };
    for (var version = 1; version < versions; version++) {
      switch (version % 5) {
        case 0:
          title = $"Publication 1, corrected {version}";
          break;
        case 1:
          keywords.Add(Keyword(version));
          break;
        case 2:
          authors.Reverse();
          break;
        case 3:
          year++;
          break;
        default:
          authors.Add(Author(version));
          break;
      }
      steps.Add((0, Current()));
    }
    return new VersioningWorkload {
      Name = "Publications", Description = "the benchmark project's Publication documents with nested authors and keywords",
      Columns = columns, Records = 1, Steps = steps, ElementKeyColumn = "Authors", ElementKey = "id"
    };
  }

  //NF-3's third: an import of many rows in one go, then scattered corrections — a few
  //records corrected a few times, most never.
  public static VersioningWorkload AssistantImport(int records, int corrections) {
    var random = new Random(11);
    var columns = Enumerable.Range(0, 12)
      .Select(i => new ColumnDescriptor($"column{i:D2}", i % 4 == 0 ? ValueTypeEnum.Int : ValueTypeEnum.String)).ToList();
    Dictionary<string, IDocumentValue> Row(int record, int version) {
      var row = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
      for (var i = 0; i < 12; i++) {
        row[$"column{i:D2}"] = i % 4 == 0
          ? new IntDocumentValue(record * 100 + i + version)
          : new StringDocumentValue($"row {record} column {i} v{version}");
      }
      return row;
    }
    var current = Enumerable.Range(0, records).Select(record => Row(record, 0)).ToList();
    var steps = Enumerable.Range(0, records).Select(record => (record, Copy(current[record]))).ToList();
    for (var correction = 0; correction < corrections; correction++) {
      var record = random.Next(records);
      var next = Copy(current[record]);
      var column = random.Next(12);
      next[$"column{column:D2}"] = column % 4 == 0
        ? new IntDocumentValue(random.Next(100_000))
        : new StringDocumentValue($"corrected {correction}");
      current[record] = next;
      steps.Add((record, Copy(next)));
    }
    return new VersioningWorkload {
      Name = "Assistant import and corrections", Description = $"{records} rows imported, then {corrections} scattered corrections",
      Columns = columns, Records = records, Steps = steps
    };
  }
}
