using System.Reflection;
using TokkDb.Pages.Versions;
using Xunit;

namespace TokkDb.Tests.Architecture;

//HS-3's two rules, checked over the compiled engine assemblies rather than promised in a
//comment: only the write seam and schema changes call the store's recording operations, and
//nothing outside TokkDb.Pages.Versions reads a node document or the version index.
public class VersionStoreArchitectureTests {
  private static readonly Assembly[] Engine = [typeof(TokkDbConnection).Assembly, typeof(IVersionStore).Assembly];

  //Layer 1's public surface (§3.2): the contract and the values it hands out. Anything else in
  //the namespace is the store's own — its documents, its index, its class — and stays inside.
  private static readonly HashSet<string> PublicSurface = [
    nameof(IVersionStore), nameof(VersionKind), nameof(VersionNode), nameof(Operation), nameof(SchemaNode),
    nameof(RelationNode), nameof(RelationNodeKind), nameof(Reconstruction), nameof(ReconstructionReport),
    nameof(HistoryVerification), nameof(HistoryCollections), nameof(VersionAttribution),
    nameof(VersionNotFoundException), nameof(VersionHistory), nameof(VersionEntry), nameof(Unmapped),
    nameof(UnmappedReason), "VersionedValue`1", nameof(StoredVersion), nameof(AsOfOutcome), "AsOfResult`1",
    nameof(SchemaSnapshot), nameof(LogicalTime), nameof(SchemaMapping), nameof(VersionDiff)
  ];

  //The callers §3 allows: the write seam in DbEntities, and the schema changes, which live in
  //TokkDbConnection.
  private static readonly HashSet<string> RecordingCallers = ["DbEntities`1", nameof(TokkDbConnection)];

  private static bool InVersions(Type type) {
    return type.Namespace == typeof(IVersionStore).Namespace;
  }

  private static bool IsRecording(MemberInfo member) {
    return member is MethodInfo method
      && (method.DeclaringType == typeof(IVersionStore) || method.DeclaringType == typeof(VersionStore))
      && method.Name.StartsWith("Record", StringComparison.Ordinal);
  }

  [Fact]
  public void OnlyTheWriteSeamAndSchemaChangesCallTheRecordingOperations() {
    var offenders = new List<string>();
    foreach (var assembly in Engine) {
      foreach (var (owner, method) in IlReferences.Methods(assembly)) {
        if (InVersions(owner) || RecordingCallers.Contains(owner.Name)) {
          continue;
        }
        foreach (var member in IlReferences.ReferencedMembers(method)) {
          if (IsRecording(member)) {
            offenders.Add($"{owner.FullName}.{method.Name} calls {member.DeclaringType!.Name}.{member.Name}");
          }
        }
      }
    }
    Assert.Empty(offenders);
  }

  [Fact]
  public void NothingOutsideTheStoreReadsANodeDocumentOrTheVersionIndex() {
    var offenders = new List<string>();
    foreach (var assembly in Engine) {
      foreach (var (owner, method) in IlReferences.Methods(assembly)) {
        if (InVersions(owner)) {
          continue;
        }
        foreach (var member in IlReferences.ReferencedMembers(method)) {
          var declaring = member.DeclaringType;
          if (declaring is null || !InVersions(declaring)) {
            continue;
          }
          //A type nested in a public one — a mapping's result record — is part of that surface.
          var outermost = declaring;
          while (outermost.DeclaringType is not null) {
            outermost = outermost.DeclaringType;
          }
          if (PublicSurface.Contains(outermost.Name)) {
            continue;
          }
          //The composition root makes the one store; that is the only thing it may do with it.
          if (owner == typeof(TokkDbConnection) && method.IsConstructor && member is ConstructorInfo
              && declaring == typeof(VersionStore)) {
            continue;
          }
          offenders.Add($"{owner.FullName}.{method.Name} uses {declaring.Name}.{member.Name}");
        }
      }
    }
    Assert.Empty(offenders);
  }

  //The scanner itself sees what it should: a method known to call the store shows up.
  [Fact]
  public void TheScannerSeesTheCompositionRootsUseOfTheStore() {
    var constructor = typeof(TokkDbConnection).GetConstructors()
      .Single(candidate => candidate.GetParameters().Length == 1);
    Assert.Contains(IlReferences.ReferencedMembers(constructor),
      member => member is ConstructorInfo && member.DeclaringType == typeof(VersionStore));
  }
}
