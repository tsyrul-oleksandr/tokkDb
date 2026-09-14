using System.Reflection;
using System.Reflection.Emit;
using TokkDb.Pages.Query;
using TokkDb.Pages.Query.Pipeline;
using TokkDb.Tests.Architecture;
using Xunit;

namespace TokkDb.Tests;

//DG-4: the execution is explicit stages, and every counter is an event of exactly one of them.
//The table below is the table of the requirement; a counter that moved to another stage, or that
//a second place learned to increment, fails here.
public class PipelineOwnershipTests {
  private static readonly (Type Stage, string[] Counters, string Report)[] Table = [
    (typeof(CandidateEnumeration), ["PagesRead", "RecordsExamined", "IndexProbes"], "walks the access path"),
    (typeof(PredicateEvaluation), ["RecordsMatched"], "conjuncts then residual, against the record on the page"),
    (typeof(OrderingStage), ["Source", "RecordsRetained", "BytesRetained"], "index walk, bounded heap, or complete sort"),
    (typeof(PagingStage), ["RecordsSkipped", "RecordsReturned"], "skip and take, inside the ordering stage"),
    (typeof(MaterialisationStage), ["DocumentsMaterialised"], "only surviving records of the page become documents"),
    (typeof(ReportFinalisation), ["Elapsed"], "closes the figures, after enumeration ends")
  ];

  private static readonly Type[] Stages = Table.Select(row => row.Stage).ToArray();

  [Fact]
  public void TheStagesAreTheSixOfTheTable() {
    var pipeline = typeof(QueryPipeline).Assembly.GetTypes()
      .Where(type => type.Namespace == typeof(QueryPipeline).Namespace && type.Name.EndsWith("Stage") || type.Name == nameof(CandidateEnumeration)
        || type.Name == nameof(PredicateEvaluation) || type.Name == nameof(ReportFinalisation))
      .Where(type => type.Namespace == typeof(QueryPipeline).Namespace)
      .OrderBy(type => type.Name)
      .ToArray();

    Assert.Equal(Stages.OrderBy(type => type.Name), pipeline);
  }

  //Each counter is a readable member of its own stage, with no public setter, and of no other
  //stage. If RecordsMatched moved into the enumeration, or the enumeration grew a RecordsMatched
  //of its own, this is the test that fails.
  [Fact]
  public void EachCounterIsDeclaredByExactlyItsStage() {
    foreach (var (stage, counters, _) in Table) {
      foreach (var counter in counters) {
        var property = stage.GetProperty(counter, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.True(property is not null, $"{stage.Name} does not declare {counter}");
        Assert.True(property.GetMethod is { IsPublic: true }, $"{stage.Name}.{counter} is not readable");
        Assert.True(property.SetMethod is null or { IsPublic: false }, $"{stage.Name}.{counter} can be set from outside its stage");
        foreach (var other in Stages.Where(other => other != stage)) {
          Assert.True(other.GetProperty(counter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is null,
            $"{other.Name} also declares {counter}, which {stage.Name} owns");
        }
      }
    }
  }

  //Every store to a field of a stage is made by a method of that stage: the counters are
  //incremented where they are owned and nowhere else, checked in the IL so that a lambda or a
  //nested type is held to the same rule.
  [Fact]
  public void ACounterIsIncrementedNowhereButInItsStage() {
    var stores = new[] { OpCodes.Stfld, OpCodes.Stsfld };
    var methods = IlReferences.Methods(typeof(QueryPipeline).Assembly).ToList();
    foreach (var stage in Stages) {
      var fields = stage.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).ToHashSet();
      Assert.NotEmpty(fields);
      foreach (var (owner, method) in methods) {
        foreach (var (opCode, member) in IlReferences.ReferencedMembersWithOpCodes(method)) {
          if (member is FieldInfo field && fields.Contains(field) && stores.Contains(opCode)) {
            Assert.True(owner == stage, $"{owner.Name}.{method.Name} stores {stage.Name}.{field.Name}");
          }
        }
      }
    }
  }

  //The report's figures are the stages' figures, under the names the table gives them.
  [Fact]
  public void TheReportCarriesEachStagesFigures() {
    var report = typeof(QueryReport);
    var figures = new Dictionary<string, string> {
      ["PagesRead"] = "PagesRead", ["RecordsExamined"] = "RecordsExamined", ["IndexProbes"] = "IndexProbes",
      ["RecordsMatched"] = "RecordsMatched", ["Source"] = "OrderSource", ["RecordsRetained"] = "RecordsRetained",
      ["BytesRetained"] = "OrderingStageBytes", ["RecordsSkipped"] = "RecordsSkipped", ["RecordsReturned"] = "RecordsReturned",
      ["DocumentsMaterialised"] = "DocumentsMaterialised", ["Elapsed"] = "Elapsed"
    };
    foreach (var (counter, figure) in figures) {
      Assert.True(report.GetProperty(figure) is not null, $"QueryReport has no {figure} for {counter}");
    }
    Assert.Equal(figures.Keys.Order(), Table.SelectMany(row => row.Counters).Order());
  }
}
