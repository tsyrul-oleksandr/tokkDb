using TokkDb.Assistant.Diagram;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Trace to geometry (7.1, TR-5) and the one transform (7.2, TR-5a): a fifteen-step trace with a
/// nested call lays out as rows on the right lanes, the nested step sits inside its parent, and a
/// click at the same logical point lands on the same block at any scale.
/// </summary>
public sealed class DiagramLayoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Fifteen steps, as a store request records them, with the model's second call nested inside the placement.</summary>
    private static List<ExecutionStep> FifteenSteps()
    {
        var request = Ulid.NewUlid();
        var steps = new List<ExecutionStep>();
        Ulid? last = null;
        var t = Start;

        ExecutionStep Add(string name, TimeSpan took, string? input = null, string? output = null, ModelCall? call = null, DateTimeOffset? at = null, bool running = false)
        {
            var started = at ?? t;
            var step = new ExecutionStep(Ulid.NewUlid(), request, name, running ? StepStatus.Running : StepStatus.Completed, started, running ? null : started + took)
            {
                After = last, Input = input, Output = output, Call = call
            };
            steps.Add(step);
            last = step.Id;
            if (at is null) t += took;
            return step;
        }

        var call = new ModelCall("qwen3.5:4b", 700, 40, TimeSpan.FromSeconds(2), "abc123");

        Add("the person", TimeSpan.Zero, input: "Here are more of them");                     // 1 person
        Add("the assistant", TimeSpan.Zero, output: "work out what is meant");                 // 2 assistant
        Add("what was meant", TimeSpan.FromSeconds(2), output: "store", call: call);            // 3 model
        Add("what is in the file", TimeSpan.FromMilliseconds(20), output: "4 rows");             // 4 assistant
        var placement = Add("the placement", TimeSpan.FromSeconds(3), input: "4 rows of 5");      // 5 assistant, encloses 6 and 7
        Add("where it belongs", TimeSpan.FromSeconds(2), output: "conferences", call: call, at: placement.StartedAt + TimeSpan.FromMilliseconds(100)); // 6 model, nested
        Add("the check", TimeSpan.FromMilliseconds(10), output: "confident", at: placement.StartedAt + TimeSpan.FromMilliseconds(2200));           // 7 assistant, nested
        t = placement.EndedAt!.Value;
        Add("the new field", TimeSpan.FromMilliseconds(30), output: "notes");                    // 8 storage
        Add("the write", TimeSpan.FromMilliseconds(80), output: "4 kept");                        // 9 storage
        Add("the answer", TimeSpan.Zero, output: "Kept 4 more of conferences.");                  // 10 assistant
        Add("the person", TimeSpan.Zero, input: "How much did I spend?");                         // 11 person
        Add("the assistant", TimeSpan.Zero);                                                     // 12 assistant
        Add("what to look for", TimeSpan.FromSeconds(1), output: "conferences in 2025", call: call); // 13 model
        Add("looking", TimeSpan.FromMilliseconds(15), output: "6 matched");                       // 14 storage
        Add("the answer", TimeSpan.Zero, running: true);                                          // 15 assistant, still running

        return steps;
    }

    [Fact]
    public void A_fifteen_step_trace_lays_out_as_rows_on_the_right_lanes_with_the_nested_call_inside_its_parent()
    {
        var steps = FifteenSteps();

        var layout = DiagramLayouts.Layout(steps);

        Assert.Equal(15, layout.Blocks.Count);
        Assert.Equal(4, layout.Lanes.Count);
        Assert.Equal(["You", "The assistant", "The model", "What is stored"], layout.Lanes.Select(static lane => lane.Title));

        // Rows in trace order, each a row height apart, all inside the extent.
        for (var i = 0; i < layout.Blocks.Count; i++)
        {
            Assert.Equal(i, layout.Blocks[i].Row);
            Assert.Equal(steps[i].Id, layout.Blocks[i].StepId);
            Assert.True(layout.Blocks[i].Y + layout.Blocks[i].Height <= layout.Height);
            Assert.True(layout.Blocks[i].X >= 0 && layout.Blocks[i].X + layout.Blocks[i].Width <= layout.Width);
        }

        Assert.Equal(LayoutOptions.Default.RowHeight, layout.Blocks[1].Y - layout.Blocks[0].Y);

        // The lanes: who did the work.
        Assert.Equal(Participant.Person, layout.Blocks[0].Lane);
        Assert.Equal(Participant.Assistant, layout.Blocks[1].Lane);
        Assert.Equal(Participant.Model, layout.Blocks[2].Lane);
        Assert.Equal(Participant.Storage, layout.Blocks[7].Lane);
        Assert.Equal(Participant.Storage, layout.Blocks[8].Lane);
        Assert.Equal(Participant.Storage, layout.Blocks[13].Lane);
        Assert.True(layout.Blocks[2].IsModelCall);

        // The nested call: inside the placement, indented, and the parent covers both children.
        var placement = layout.Blocks[4];
        var mapping = layout.Blocks[5];
        var check = layout.Blocks[6];
        Assert.Equal(0, placement.Depth);
        Assert.Equal(1, mapping.Depth);
        Assert.Equal(1, check.Depth);
        Assert.Equal(placement.StepId, mapping.ParentId);
        Assert.Equal(placement.StepId, check.ParentId);
        Assert.Null(placement.ParentId);
        Assert.True(placement.Y + placement.Height >= check.Y + check.Height);
        Assert.Equal(3 * LayoutOptions.Default.RowHeight - LayoutOptions.Default.RowGap, placement.Height);
        Assert.Equal(LayoutOptions.Default.RowHeight - LayoutOptions.Default.RowGap, mapping.Height);
        Assert.True(check.X > layout.Blocks[3].X, "a nested step on the assistant lane is indented past an unnested one");
        Assert.Equal(0, layout.Blocks[7].Depth);

        // Calls go out and come back; the running last step has no return yet.
        Assert.Contains(layout.Arrows, arrow => arrow.StepId == steps[2].Id && !arrow.IsReturn && arrow.Label == "what was meant");
        Assert.Contains(layout.Arrows, arrow => arrow.StepId == steps[13].Id && !arrow.IsReturn);
        Assert.DoesNotContain(layout.Arrows, arrow => arrow.StepId == steps[14].Id);
        Assert.DoesNotContain(layout.Arrows, arrow => arrow.StepId == steps[1].Id);
        Assert.Equal("running", layout.Blocks[14].Summary);
        Assert.Equal(StepStatus.Running, layout.Blocks[14].Status);
    }

    [Fact]
    public void Steps_come_out_in_trace_order_however_they_were_read()
    {
        var steps = FifteenSteps();
        var shuffled = steps.OrderBy(static step => step.Name).ThenBy(static step => step.Id).ToList();

        var ordered = DiagramLayouts.InTraceOrder(shuffled);

        Assert.Equal(steps.Select(static step => step.Id), ordered.Select(static step => step.Id));

        // A step recorded twice - running, then completed - counts once, as its latest recording.
        var twice = new List<ExecutionStep>(steps) { steps[3] with { Status = StepStatus.Failed, Output = "could not read it" } };
        var once = DiagramLayouts.Layout(twice);
        Assert.Equal(15, once.Blocks.Count);
        Assert.Equal(StepStatus.Failed, once.Blocks[3].Status);
    }

    [Fact]
    public void The_same_logical_point_lands_on_the_same_block_at_every_scale()
    {
        var layout = DiagramLayouts.Layout(FifteenSteps());
        var mapping = layout.Blocks[5];
        var placement = layout.Blocks[4];
        var write = layout.Blocks[8];

        var insideMapping = (X: mapping.X + mapping.Width / 2, Y: mapping.Y + mapping.Height / 2);
        var insidePlacementOnly = (X: placement.X + 2, Y: placement.Y + placement.Height - 2);
        var insideWrite = (X: write.X + 1, Y: write.Y + 1);
        var nowhere = (X: 1.0, Y: 1.0);

        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var transform = new DiagramTransform(scale, OffsetX: 7, OffsetY: 3);

            // The point drawn at this scale, clicked at this scale, is the block it was drawn from.
            var (dx, dy) = transform.ToDevice(insideMapping.X, insideMapping.Y);
            Assert.Equal(mapping.StepId, HitTesting.BlockAt(layout, transform, dx, dy)?.StepId);

            (dx, dy) = transform.ToDevice(insidePlacementOnly.X, insidePlacementOnly.Y);
            Assert.Equal(placement.StepId, HitTesting.BlockAt(layout, transform, dx, dy)?.StepId);

            (dx, dy) = transform.ToDevice(insideWrite.X, insideWrite.Y);
            Assert.Equal(write.StepId, HitTesting.BlockAt(layout, transform, dx, dy)?.StepId);

            (dx, dy) = transform.ToDevice(nowhere.X, nowhere.Y);
            Assert.Null(HitTesting.BlockAt(layout, transform, dx, dy));

            // And the transform is its own inverse.
            var (lx, ly) = transform.ToLogical(dx, dy);
            Assert.Equal(nowhere.X, lx, 9);
            Assert.Equal(nowhere.Y, ly, 9);
        }

        // Fitting a narrow view shrinks and never enlarges; a wide view centres.
        var narrow = DiagramTransform.FitWidth(layout, layout.Width / 2);
        Assert.Equal(0.5, narrow.Scale, 9);
        Assert.Equal(0, narrow.OffsetX, 9);
        var wide = DiagramTransform.FitWidth(layout, layout.Width * 2);
        Assert.Equal(1, wide.Scale, 9);
        Assert.Equal(layout.Width / 2, wide.OffsetX, 9);
    }

    [Fact]
    public void An_empty_trace_is_lanes_and_nothing_else()
    {
        var layout = DiagramLayouts.Layout([]);

        Assert.Empty(layout.Blocks);
        Assert.Empty(layout.Arrows);
        Assert.Equal(4, layout.Lanes.Count);
        Assert.Null(HitTesting.BlockAt(layout, 10, 10));
    }
}
