using System.Text.RegularExpressions;
using Xunit;

namespace TokkDb.Tests.Architecture;

//HS-3's source-level rule. Exactly four paths may change a versioned record's stored image:
//the write seam, Rewrite, Erase and DropCollection. The methods that free, retire or rewrite
//an image in place — RetireRow, UpdateRow, RewriteRow and FreeItem — are listed here with every
//call site in the engine projects and one reason per site why it cannot reach a versioned
//collection's records outside those paths. A call site the list does not name fails the
//test, and so does a listed site that is no longer there.
public class RetirementCallSiteTests {
  private static readonly string[] Projects = ["TokkDb", "TokkDb.Pages", "TokkDb.Disk"];

  private static readonly Regex Call = new(@"\b(RetireRow|UpdateRow|RewriteRow|FreeItem)\(", RegexOptions.Compiled);
  private static readonly Regex Declaration = new(@"^\s*(public|private|protected|internal|static|override|virtual|abstract)\b",
    RegexOptions.Compiled);
  private static readonly Regex Member = new(@"^\s*(public|private|protected|internal)[^=;(]*?\b(\w+)\s*\(", RegexOptions.Compiled);

  //(file, enclosing member, the call as written) → why it is safe.
  private static readonly Dictionary<(string File, string Member, string Call), string> Recorded = new() {
    [("TokkDb/DbEntities.cs", "RemoveCurrentVersion", "_dataPageManager.RetireRow(_entityName, current.Address, flags);")] =
      "the write seam: the one retirement of a user record's image, which records the version first (step 3.2)",
    [("TokkDb/TokkDbConnection.cs", "DropCollection", "_dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted);")] =
      "DropCollection: removes the records together with their history (HS-2), one of the four paths",
    [("TokkDb.Pages/Relations/RelationCatalog.cs", "Remove", "_dataPageManager.RetireRow(SystemCollections.Relations, row.Address, RecordFlags.Deleted);")] =
      "_relations only, a reserved collection that keeps no versions (F-6)",
    [("TokkDb.Pages/CollectionCatalog/CollectionCatalog.cs", "DropCollectionCore", "_dataPageManager.RetireRow(SystemCollections.Collections, address, RecordFlags.Deleted);")] =
      "_collections only: the descriptor document of the dropped collection",
    [("TokkDb.Pages/CollectionCatalog/CollectionCatalog.cs", "Append", "_dataPageManager.UpdateRow(row.Address, header, CollectionDescriptorDocument.Write(descriptor));")] =
      "_collections only: a descriptor written moments ago, given its final record count",
    [("TokkDb.Pages/CollectionCatalog/CollectionCatalog.cs", "Save", "_dataPageManager.UpdateRow(descriptor.Address.Value, header, document);")] =
      "_collections only: a descriptor rewritten where it lies",
    [("TokkDb.Pages/CollectionCatalog/CollectionCatalog.cs", "Save", ".RewriteRow(SystemCollections.Collections, descriptor.Address.Value, header, document).Address;")] =
      "_collections only: a descriptor that outgrew its slot",
    [("TokkDb.Pages/CollectionCatalog/CollectionCatalog.cs", "MoveSelf", "return _dataPageManager.RewriteRow(SystemCollections.Collections, address, header, document).Address;")] =
      "_collections only: the catalogue's own descriptor, when it outgrew its slot",
    [("TokkDb.Pages/Managers/DataPageManager.cs", "MigrateRow", "UpdateRow(row.Address, header, document);")] =
      "MigrateRow is called only by Rewrite, one of the four paths, which preserves what it would lose (WV-8, step 3.4)",
    [("TokkDb.Pages/Managers/DataPageManager.cs", "MigrateRow", "var moved = RewriteRow(collectionName, row.Address, header, document);")] =
      "MigrateRow is called only by Rewrite, one of the four paths (WV-8)",
    [("TokkDb.Pages/Managers/DataPageManager.cs", "RewriteRow", "page.FreeItem(address.SlotIndex);")] =
      "RewriteRow frees the slot it moves out of; a user record reaches it only through MigrateRow, so through Rewrite",
    [("TokkDb.Pages/Managers/DataPageManager.cs", "RetireRow", "page.FreeItem(address.SlotIndex);")] =
      "RetireRow frees the retired image's slot; a user record reaches it only through the seam, DropCollection or (step 7.3) Erase",
    [("TokkDb.Pages/Managers/SystemDocumentStore.cs", "Write", "_dataPageManager.UpdateRow(row.Value.Address, header, document);")] =
      "reserved collections only: Require refuses any other name",
    [("TokkDb.Pages/Managers/SystemDocumentStore.cs", "Write", "_dataPageManager.RetireRow(collectionName, row.Value.Address, RecordFlags.Superseded);")] =
      "reserved collections only: Require refuses any other name",
    [("TokkDb.Pages/Managers/SystemDocumentStore.cs", "Delete", "_dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted);")] =
      "reserved collections only: Require refuses any other name",
    [("TokkDb.Pages/Indexes/IndexCatalog.cs", "Drop", "_dataPageManager.RetireRow(SystemCollections.Indexes, row.Address, RecordFlags.Deleted);")] =
      "_indexes only: the descriptor document of the dropped index",
    [("TokkDb.Pages/Versions/VersionStore.cs", "DropHistory", "_dataPageManager.RetireRow(name, row.Address, RecordFlags.Deleted);")] =
      "a history collection only, on behalf of DropCollection or V-14, and never a live image",
    [("TokkDb.Pages/Versions/VersionStore.cs", "RewriteNode", "_dataPageManager.RetireRow(history, node.Address, RecordFlags.Deleted);")] =
      "a history collection only: a node document written again with its image (WV-2 step 2), never a live image"
  };

  public static string RepositoryRoot() {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TokkDb.slnx"))) {
      directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
  }

  private static IEnumerable<(string File, string Member, string Call)> CallSites() {
    var root = RepositoryRoot();
    foreach (var project in Projects) {
      var directory = Path.Combine(root, project);
      foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (relative.Contains("/bin/") || relative.Contains("/obj/")) {
          continue;
        }
        var member = "";
        foreach (var raw in File.ReadLines(file)) {
          var line = raw.Trim();
          if (line.StartsWith("//", StringComparison.Ordinal)) {
            continue;
          }
          if (Member.Match(raw) is { Success: true } declared) {
            member = declared.Groups[2].Value;
          }
          if (Declaration.IsMatch(raw) || !Call.IsMatch(line)) {
            continue;
          }
          yield return (relative, member, line);
        }
      }
    }
  }

  [Fact]
  public void EveryCallSiteIsRecordedWithItsReason() {
    var found = CallSites().ToList();
    var unrecorded = found.Where(site => !Recorded.ContainsKey(site)).ToList();
    var stale = Recorded.Keys.Where(site => !found.Contains(site)).ToList();

    Assert.True(unrecorded.Count == 0,
      "call sites the list does not name:\n" + string.Join("\n", unrecorded.Select(site => $"  {site.File} in {site.Member}: {site.Call}")));
    Assert.True(stale.Count == 0,
      "recorded call sites that no longer exist:\n" + string.Join("\n", stale.Select(site => $"  {site.File} in {site.Member}: {site.Call}")));
    Assert.All(Recorded.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    Assert.Equal(Recorded.Count, found.Count);
  }
}
