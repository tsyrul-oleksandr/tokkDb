using System.Reflection;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The dependency graph of §3.1, asserted rather than described.
///
/// <b>Why this is a test and not a diagram in a document.</b> An earlier version of §3.1 declared
/// that the orchestrator depends on the diagram and that the diagram depends on the orchestrator.
/// That is not a design weakness, it is a project reference cycle, and it was in a requirements
/// document for three drafts because nothing could disagree with it. This can.
///
/// It also carries SC-1: no assistant project references any <c>TokkDb.LLM.*</c> assembly, which
/// D-1 requires and which is the kind of thing one convenient reference quietly ends.
/// </summary>
public sealed class ProjectGraphTests
{
    /// <summary>
    /// What each project is allowed to depend on, from §3.1. Everything not named here is
    /// forbidden, which is the direction that matters: a list of permissions catches the
    /// reference nobody meant to add, and a list of prohibitions only catches the ones somebody
    /// thought of.
    /// </summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["TokkDb.Assistant.Trace"] = [],
        ["TokkDb.Assistant.Storage"] = [],
        ["TokkDb.Assistant.Ingestion"] = [],
        ["TokkDb.Assistant.Agents"] =
            ["TokkDb.Assistant.Storage", "TokkDb.Assistant.Ingestion", "TokkDb.Assistant.Trace"],
        ["TokkDb.Assistant.Diagram"] = ["TokkDb.Assistant.Trace"],
        ["TokkDb.Assistant.Storage.Engine"] =
            ["TokkDb.Assistant.Storage", "TokkDb.Assistant.Trace", "TokkDb"],
        ["TokkDb.Assistant.TokenBudget"] =
        [
            "TokkDb.Assistant.Trace", "TokkDb.Assistant.Agents", "TokkDb.Assistant.Storage",
            "TokkDb.Assistant.Storage.Engine"
        ],
        ["TokkDb.Assistant.App"] =
        [
            "TokkDb.Assistant.Agents", "TokkDb.Assistant.Diagram", "TokkDb.Assistant.Trace",
            "TokkDb.Assistant.Storage.Engine"
        ]
    };

    /// <summary>
    /// The assistant assemblies that exist today. The graph covers the ones that do not yet, so
    /// that the rule is in place before the project that would break it is written.
    /// </summary>
    private static IReadOnlyList<Assembly> Built =>
    [
        typeof(IStorage).Assembly,
        typeof(TokkDbStorage).Assembly,
        typeof(Table).Assembly,
        typeof(RequestTrace).Assembly
    ];

    [Fact]
    public void Every_assistant_project_depends_only_on_what_the_plan_allows()
    {
        var broken = new List<string>();

        foreach (var assembly in Built)
        {
            var name = assembly.GetName().Name!;
            var allowed = Allowed[name];

            foreach (var reference in TokkDbReferences(assembly))
            {
                if (!allowed.Contains(reference, StringComparer.Ordinal))
                {
                    broken.Add($"{name} -> {reference}");
                }
            }
        }

        Assert.Empty(broken);
    }

    /// <summary>
    /// SC-1 and D-1. The old application is a reference to read, not a dependency to take: it was
    /// shaped by a walking skeleton and carries decisions made under other constraints, and the
    /// whole point of rebuilding the contract is arriving at those answers on purpose.
    /// </summary>
    [Fact]
    public void Nothing_in_the_assistant_references_the_old_application()
    {
        foreach (var assembly in Built)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name!.StartsWith("TokkDb.LLM", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The graph runs one way only. Asserted over the whole of §3.1 rather than over what is
    /// built, because the edge that was wrong was between two projects that did not exist yet.
    /// </summary>
    [Fact]
    public void The_graph_is_acyclic()
    {
        foreach (var project in Allowed.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var path = new Stack<string>();

            Assert.False(
                ReachesItself(project, project, seen, path),
                $"{project} depends on itself, through {string.Join(" -> ", path.Reverse())}");
        }
    }

    /// <summary>
    /// §3.1's other half, and the quieter of the two errors it corrected: the orchestrator has no
    /// reason to depend on the diagram at all. Layout is presentation. Nothing references the
    /// diagram except the application, which is what makes it impossible for a change to the
    /// drawing to invalidate an orchestration test.
    /// </summary>
    [Fact]
    public void Nothing_but_the_application_references_the_diagram()
    {
        foreach (var (project, references) in Allowed)
        {
            if (project is "TokkDb.Assistant.App" or "TokkDb.Assistant.Diagram") continue;

            Assert.DoesNotContain("TokkDb.Assistant.Diagram", references);
        }
    }

    private static bool ReachesItself(string from, string looking, HashSet<string> seen, Stack<string> path)
    {
        foreach (var next in Allowed.GetValueOrDefault(from, []))
        {
            path.Push(next);

            if (string.Equals(next, looking, StringComparison.Ordinal)) return true;
            if (seen.Add(next) && ReachesItself(next, looking, seen, path)) return true;

            path.Pop();
        }

        return false;
    }

    /// <summary>
    /// The references that are ours, as the graph names them. The framework's and the packages'
    /// are not part of it: §3.1 is about which of these projects may know about which, and Ulid is
    /// not one of them.
    ///
    /// <b>The engine is one node.</b> §3.1 writes it as "TokkDb", and it is eight assemblies -
    /// the pages, the documents, the disk, the buffer and the rest - which a project takes
    /// together or not at all. Listing them separately would turn one decision into eight lines
    /// that mean the same thing.
    /// </summary>
    private static IEnumerable<string> TokkDbReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name!)
            .Where(static name => name.StartsWith("TokkDb", StringComparison.Ordinal))
            .Select(static name => name.StartsWith("TokkDb.Assistant", StringComparison.Ordinal)
                                   || name.StartsWith("TokkDb.LLM", StringComparison.Ordinal)
                ? name
                : "TokkDb")
            .Distinct(StringComparer.Ordinal);
}
