using System.Diagnostics;
using TokkDb.Documents.Keys;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;

namespace TokkDb.Benchmarks.Benchmarks;

//DC-4 and D-2: what a lookup costs once the identity has an index behind it, and D-1: what
//a time-ordered identity saves the tree that a random one would not.
public class PrimaryIndexBenchmark : IBenchmark {
  public string Name => "Primary index";

  public string Description =>
    "Lookup by record identity through the B+Tree, and what a time-ordered identity costs it.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    using var db = new TokkDbConnection(context.PopulatedDatabasePath);
    db.Load();
    var entities = db.Entities<Publication>();
    var tree = db.PrimaryIndex(nameof(Publication));
    var identities = context.SampleRecordIds;
    var height = tree.Height();
    var leaves = tree.Leaves().Count();

    _ = entities.GetById(identities[0]);

    var stopwatch = Stopwatch.StartNew();
    var found = 0;
    foreach (var identity in identities) {
      if (entities.GetById(identity) is not null) {
        found++;
      }
    }
    stopwatch.Stop();

    //One lookup on its own, so the counter reports a descent rather than an average over a
    //warmed page cache.
    var readsBefore = db.PageReadCount;
    _ = entities.GetById(identities[^1]);
    var readsPerLookup = db.PageReadCount - readsBefore;

    var scattered = MeasureControlTree(context);

    return [
      new Measurement(Name, "Lookup by record id", stopwatch.Elapsed.TotalMilliseconds / identities.Count,
        "ms", "NFR-2", 10, $"One descent of the tree, {found} of {identities.Count} targets found."),
      new Measurement(Name, "Pages read per lookup", readsPerLookup, "pages", "DC-4",
        Note: $"A tree of height {height} over {context.RecordCount:N0} records: {height} index pages and " +
          "the one data page the entry addresses. O(log n), against the whole collection for a scan."),
      new Measurement(Name, "Index height", height, "levels", Note:
        $"{leaves:N0} leaves holding {context.RecordCount / Math.Max(1, leaves):N0} entries each."),
      new Measurement(Name, "Leaf splits building the index", context.IndexLeafSplits, "splits", "D-1",
        Note: $"{scattered.LeafSplits:N0} for the same number of random identities — the comparison D-1 " +
          "rests on. A monotonic identity appends, so only the rightmost leaf ever splits and the " +
          "leaves behind it stay full."),
      new Measurement(Name, "Index pages", leaves + tree.InteriorSplits + 1, "pages", Note:
        $"{scattered.Pages:N0} for random identities, which is what a Guid identity would have cost.")
    ];
  }

  //What the control tree cost, read while its database is still open — a tree is a view over
  //pages, so it says nothing once the file behind it is closed.
  private readonly record struct ControlTree(long LeafSplits, int Leaves, int Pages);

  //The same number of entries under identities with no time in them, which is what a Guid
  //would have given. In a database of its own, so the numbers of the benchmarks that read
  //the populated one are not moved by it.
  private static ControlTree MeasureControlTree(BenchmarkContext context) {
    var path = context.CreateDatabasePath("primary-index-control");
    using var db = new TokkDbConnection(path);
    db.CreateDatabase(config => config.CreateEntity<Publication>());
    var tree = db.PrimaryIndex(nameof(Publication));
    var random = new Random(17);
    db.InTransaction(() => {
      for (var i = 0; i < context.RecordCount; i++) {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        tree.Insert(KeyEncoder.Encode(new Ulid(bytes)).Bytes,
          new DocumentAddress((uint)(i / 50 + 1), (ushort)(i % 50)));
      }
    });
    var leaves = tree.Leaves().Count();
    return new ControlTree(tree.LeafSplits, leaves, leaves + (int)tree.InteriorSplits + 1);
  }
}
