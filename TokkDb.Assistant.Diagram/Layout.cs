using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Diagram;

/// <summary>Who takes part: the lanes of the sequence, left to right, in the words the person sees.</summary>
public enum Participant
{
    Person = 0,
    Assistant,
    Model,
    Storage
}

/// <summary>One lane: a participant, its title, and the x of its centre line in points.</summary>
public sealed record Lane(Participant Who, string Title, double X);

/// <summary>
/// One step as a block on its lane: where it is in points, how deep it is nested, and what it
/// was, so that the drawn block, the focusable list item (UI-8) and the hit test all come from
/// the same record.
/// </summary>
public sealed record Block(
    Ulid StepId,
    string Name,
    Participant Lane,
    int Row,
    int Depth,
    double X,
    double Y,
    double Width,
    double Height,
    StepStatus Status,
    bool IsModelCall,
    TimeSpan? Took,
    string? Summary,
    Ulid? ParentId)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>A call from the assistant to another participant, or its answer coming back.</summary>
public sealed record Arrow(Ulid StepId, double FromX, double ToX, double Y, string Label, bool IsReturn);

/// <summary>The whole diagram in points: lanes, blocks in trace order, arrows, and its extent.</summary>
public sealed record DiagramLayout(
    IReadOnlyList<Lane> Lanes,
    IReadOnlyList<Block> Blocks,
    IReadOnlyList<Arrow> Arrows,
    double Width,
    double Height)
{
    public static readonly DiagramLayout Empty = new([], [], [], 0, 0);
}

/// <summary>The measures of the layout, in points. Nothing here is a fraction of a viewport (TR-5a).</summary>
public sealed record LayoutOptions
{
    public double LaneWidth { get; init; } = 168;
    public double LeftMargin { get; init; } = 16;
    public double TopMargin { get; init; } = 52;
    public double RowHeight { get; init; } = 40;
    public double BlockInset { get; init; } = 14;
    public double RowGap { get; init; } = 8;
    public double Indent { get; init; } = 12;
    public int SummaryLength { get; init; } = 48;

    /// <summary>The steps that touch what is stored, by name; a step that recorded a change is one whatever its name.</summary>
    public IReadOnlySet<string> StorageSteps { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "looking", "the write", "the change", "the new thing", "the new field", "the undo", "the restore", "putting it back"
    };

    public static readonly LayoutOptions Default = new();
}

/// <summary>
/// Trace to geometry (TR-5, 7.1). Each step is a row; its lane is who did the work - the person
/// for what was said, the model for a call, what is stored for a read or a write, the assistant
/// for everything decided in C#. A step that began while another was still running is nested
/// inside it: indented, and the parent's block grows to cover it. Every measure is in points,
/// so the same layout is drawn at any scale by one transform and hit-tested by the same one.
/// </summary>
public static class DiagramLayouts
{
    public static DiagramLayout Layout(IReadOnlyList<ExecutionStep> steps, LayoutOptions? options = null, IReadOnlySet<Ulid>? stepsWithChanges = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        options ??= LayoutOptions.Default;
        stepsWithChanges ??= new HashSet<Ulid>();

        var lanes = new List<Lane>
        {
            new(Participant.Person, "You", options.LeftMargin + options.LaneWidth * 0.5),
            new(Participant.Assistant, "The assistant", options.LeftMargin + options.LaneWidth * 1.5),
            new(Participant.Model, "The model", options.LeftMargin + options.LaneWidth * 2.5),
            new(Participant.Storage, "What is stored", options.LeftMargin + options.LaneWidth * 3.5)
        };
        var width = options.LeftMargin * 2 + options.LaneWidth * lanes.Count;

        if (steps.Count == 0) return new DiagramLayout(lanes, [], [], width, options.TopMargin);

        // Trace order: the chain of After where it is whole, else by start time. A step whose
        // predecessor is missing (still to arrive, or purged) falls back to time.
        var ordered = InTraceOrder(steps);

        // Nesting from time: a step is inside the innermost step that had started and not yet
        // ended when it began.
        var depth = new int[ordered.Count];
        var parent = new Ulid?[ordered.Count];
        var lastDescendantRow = new int[ordered.Count];
        var open = new Stack<int>();

        for (var row = 0; row < ordered.Count; row++)
        {
            var step = ordered[row];
            while (open.Count > 0 && !Encloses(ordered[open.Peek()], step)) open.Pop();

            depth[row] = open.Count;
            parent[row] = open.Count > 0 ? ordered[open.Peek()].Id : null;
            lastDescendantRow[row] = row;
            foreach (var ancestor in open) lastDescendantRow[ancestor] = row;

            open.Push(row);
        }

        var blocks = new List<Block>(ordered.Count);
        var arrows = new List<Arrow>();
        var assistantX = lanes[(int)Participant.Assistant].X;

        for (var row = 0; row < ordered.Count; row++)
        {
            var step = ordered[row];
            var lane = LaneOf(step, options, stepsWithChanges);
            var laneX = lanes[(int)lane].X;
            var inset = options.BlockInset + depth[row] * options.Indent;
            var x = laneX - options.LaneWidth / 2 + inset;
            var y = options.TopMargin + row * options.RowHeight;
            var blockWidth = options.LaneWidth - inset * 2;
            var rows = lastDescendantRow[row] - row + 1;
            var height = rows * options.RowHeight - options.RowGap;

            blocks.Add(new Block(step.Id, step.Name, lane, row, depth[row], x, y, blockWidth, height, step.Status, step.Call is not null, step.Took,
                Summarise(step, options.SummaryLength), parent[row]));

            if (lane is not Participant.Assistant)
            {
                // The call goes out at the top of the block; the answer comes back at its bottom,
                // once there is one. A step still running has no return yet, which is the point.
                arrows.Add(new Arrow(step.Id, assistantX, laneX, y + options.RowHeight / 2 - options.RowGap / 2, step.Name, IsReturn: false));
                if (step.Status is not StepStatus.Running && rows > 1)
                {
                    arrows.Add(new Arrow(step.Id, laneX, assistantX, y + height, Shorten(step.Output ?? Status(step.Status), options.SummaryLength), IsReturn: true));
                }
            }
        }

        var totalHeight = options.TopMargin + ordered.Count * options.RowHeight + options.RowGap;
        return new DiagramLayout(lanes, blocks, arrows, width, totalHeight);
    }

    /// <summary>The lane a step belongs to, from what it was rather than where it was recorded.</summary>
    public static Participant LaneOf(ExecutionStep step, LayoutOptions? options = null, IReadOnlySet<Ulid>? stepsWithChanges = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        options ??= LayoutOptions.Default;

        if (step.Name == "the person") return Participant.Person;
        if (step.Call is not null) return Participant.Model;
        if (options.StorageSteps.Contains(step.Name) || stepsWithChanges?.Contains(step.Id) == true) return Participant.Storage;
        return Participant.Assistant;
    }

    /// <summary>The chain of After, followed from the first step; anything not on it comes after, by time.</summary>
    public static IReadOnlyList<ExecutionStep> InTraceOrder(IReadOnlyList<ExecutionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var byId = new Dictionary<Ulid, ExecutionStep>();
        foreach (var step in steps) byId[step.Id] = step; // the latest recording of a step wins

        var followers = new Dictionary<Ulid, List<ExecutionStep>>();
        var firsts = new List<ExecutionStep>();
        foreach (var step in byId.Values)
        {
            if (step.After is { } after && byId.ContainsKey(after))
            {
                if (!followers.TryGetValue(after, out var list)) followers[after] = list = [];
                list.Add(step);
            }
            else
            {
                firsts.Add(step);
            }
        }

        var ordered = new List<ExecutionStep>(byId.Count);
        var seen = new HashSet<Ulid>();

        void Walk(ExecutionStep step)
        {
            if (!seen.Add(step.Id)) return;
            ordered.Add(step);
            if (!followers.TryGetValue(step.Id, out var next)) return;
            foreach (var follower in next.OrderBy(static s => s.StartedAt).ThenBy(static s => s.Id)) Walk(follower);
        }

        foreach (var first in firsts.OrderBy(static s => s.StartedAt).ThenBy(static s => s.Id)) Walk(first);

        return ordered;
    }

    private static bool Encloses(ExecutionStep outer, ExecutionStep inner) =>
        outer.Id != inner.Id
        && outer.StartedAt <= inner.StartedAt
        && (outer.EndedAt is null || outer.EndedAt > inner.StartedAt || (outer.EndedAt == inner.StartedAt && outer.EndedAt != outer.StartedAt && inner.EndedAt <= outer.EndedAt));

    private static string? Summarise(ExecutionStep step, int length)
    {
        var text = step.Status switch
        {
            StepStatus.Failed => "failed: " + (step.Output ?? ""),
            StepStatus.Interrupted => "interrupted",
            StepStatus.Running => "running",
            _ => step.Output ?? step.Input
        };

        return text is null ? null : Shorten(text, length);
    }

    private static string Status(StepStatus status) => status switch
    {
        StepStatus.Completed => "done",
        StepStatus.Failed => "failed",
        StepStatus.Interrupted => "interrupted",
        _ => "running"
    };

    private static string Shorten(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }
}
