using System.Diagnostics;
using TokkDb.Pages;
using TokkDb.Pages.Versions;

namespace TokkDb.Benchmarks.Benchmarks;

//NF-2, NF-3 and NF-4: what history costs in the file and what reading a version costs in time,
//against the keyframe interval k and the large-delta ratio, over the four workloads — and
//history size against the number of versions. Every configuration replays the same operation
//sequence, so the only thing that differs between two rows is the setting under test.
//
//History size is the file's growth over the same workload under None: what versioning added,
//nodes, images, index and all. k = 1 is the full-copy layout (V-1), which is what NFR-4
//compares deltas against.
public class HistorySizeBenchmark : IBenchmark {
  private static readonly int[] Intervals = [1, 2, 4, 8, 16, 32];
  private static readonly double[] Ratios = [0.25, 0.5, 0.75, 1.0];
  private const int Versions = 100;
  private const int ReconstructionSamples = 25;

  public string Name => "History size";

  public string Description =>
    "History size and reconstruction time against the keyframe interval k and the large-delta ratio, per workload; " +
    "size ratios are against k = 1, the full-copy layout, and a ratio of 1 means the large-delta rule is off.";

  public IEnumerable<Measurement> Run(BenchmarkContext context) {
    var workloads = new[] {
      VersioningWorkload.SmallEdits(Versions),
      VersioningWorkload.ArrayAppends(Versions),
      VersioningWorkload.CompleteRewrites(Versions),
      VersioningWorkload.Publications(Versions),
      VersioningWorkload.AssistantImport(200, 300),
      VersioningWorkload.WideRecordEdits(Versions)
    };
    var measurements = new List<Measurement>();
    foreach (var workload in workloads) {
      var baseline = Replay(context, workload, RetentionPolicy.None, 8, 0.5).FileBytes;
      var fullCopy = 0L;
      foreach (var k in Intervals) {
        var run = Replay(context, workload, RetentionPolicy.KeepVersions, k, 0.5);
        var history = run.FileBytes - baseline;
        if (k == 1) {
          fullCopy = history;
        }
        measurements.Add(new Measurement(Name, $"{workload.Name}: history bytes, k = {k}", history / 1024.0, "KiB", "NFR-4",
          Note: $"{workload.Versions} versions, ratio 0.5; {(fullCopy > 0 ? $"{history / (double)fullCopy:P0} of full copy" : "the full-copy baseline")}."));
        measurements.Add(new Measurement(Name, $"{workload.Name}: worst reconstruction, k = {k}", run.WorstReconstructionMs, "ms", "NFR-2", 50,
          Note: $"at most {run.WorstDeltas} deltas applied; mean {run.MeanReconstructionMs:0.###} ms over {ReconstructionSamples} versions."));
      }
      foreach (var ratio in Ratios) {
        var run = Replay(context, workload, RetentionPolicy.KeepVersions, 8, ratio);
        var history = run.FileBytes - baseline;
        measurements.Add(new Measurement(Name, $"{workload.Name}: history bytes, k = 8, ratio {(ratio >= 1 ? "off" : ratio.ToString("0.##"))}",
          history / 1024.0, "KiB", "NFR-4",
          Note: $"{run.Keyframes} keyframes among {run.Nodes} nodes; {(fullCopy > 0 ? $"{history / (double)fullCopy:P0} of full copy" : "")}."));
      }
    }

    //NF-4: size against the number of versions, at k = 8 and ratio 0.5, and under full copy.
    var edits = VersioningWorkload.SmallEdits(Versions);
    var none = Replay(context, edits, RetentionPolicy.None, 8, 0.5, versionsToReplay: Versions).FileBytes;
    foreach (var count in new[] { 1, 2, 5, 10, 20, 50, 100 }) {
      var delta = Replay(context, edits, RetentionPolicy.KeepVersions, 8, 0.5, versionsToReplay: count).FileBytes - none;
      var full = Replay(context, edits, RetentionPolicy.KeepVersions, 1, 0.5, versionsToReplay: count).FileBytes - none;
      measurements.Add(new Measurement(Name, $"History bytes after {count} version{(count == 1 ? "" : "s")}", delta / 1024.0, "KiB", "VR-10",
        Note: $"small edits, k = 8, ratio 0.5; full copy {full / 1024.0:0.#} KiB."));
    }
    return measurements;
  }

  private sealed record Replayed(long FileBytes, double WorstReconstructionMs, double MeanReconstructionMs, int WorstDeltas,
    int Nodes, int Keyframes);

  private static Replayed Replay(BenchmarkContext context, VersioningWorkload workload, RetentionPolicy policy, int k,
      double ratio, int? versionsToReplay = null) {
    var path = context.CreateDatabasePath($"history-{workload.Name.GetHashCode():X}-{policy}-{k}-{ratio}-{versionsToReplay}");
    using var db = new TokkDbConnection(path);
    db.Load();
    db.CreateCollection("W", workload.Columns);
    db.SetRetentionPolicy("W", policy, k, ratio);
    var entities = db.Entities(new FieldMapSerializer(), "W");
    var ids = new Ulid?[workload.Records];
    var versions = new List<(Ulid Record, Ulid Version)>();
    var steps = versionsToReplay is { } limit ? workload.Steps.Take(limit).ToList() : workload.Steps;
    //One transaction, as an import runs: the size and the reads are what is measured here, not
    //the commit protocol, which the write-amplification benchmark measures.
    db.InTransaction(() => {
      foreach (var (record, document) in steps) {
        if (ids[record] is { } id) {
          entities.Update(id, document);
        } else {
          ids[record] = id = entities.Insert(document);
        }
        if (policy == RetentionPolicy.KeepVersions) {
          versions.Add((id, entities.HeadVersion(id)));
        }
      }
    });
    var fileBytes = new FileInfo(path).Length;
    if (policy == RetentionPolicy.None) {
      return new Replayed(fileBytes, 0, 0, 0, 0, 0);
    }
    var nodes = 0;
    var keyframes = 0;
    foreach (var id in ids.OfType<Ulid>()) {
      foreach (var node in db.Versions.Nodes("W", id)) {
        nodes++;
        keyframes += node.IsKeyframe ? 1 : 0;
      }
    }
    //Reconstruct a spread of versions, oldest first, each once for the cache and once measured.
    var sampled = versions.Where((_, i) => i % Math.Max(1, versions.Count / ReconstructionSamples) == 0).ToList();
    var worst = 0.0;
    var total = 0.0;
    var worstDeltas = 0;
    foreach (var (record, version) in sampled) {
      db.Versions.Reconstruct("W", record, version);
      var watch = Stopwatch.StartNew();
      var reconstruction = db.Versions.Reconstruct("W", record, version);
      watch.Stop();
      worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
      total += watch.Elapsed.TotalMilliseconds;
      worstDeltas = Math.Max(worstDeltas, reconstruction.Report.DeltasApplied);
    }
    return new Replayed(fileBytes, worst, sampled.Count == 0 ? 0 : total / sampled.Count, worstDeltas, nodes, keyframes);
  }
}
