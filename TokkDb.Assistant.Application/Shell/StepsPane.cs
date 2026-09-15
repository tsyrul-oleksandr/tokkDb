using TokkDb.Assistant.Application.Chat;
using TokkDb.Assistant.Application.Diagram;
using TokkDb.Assistant.Diagram;
using TokkDb.Assistant.Trace;
using LayoutOptions = Microsoft.Maui.Controls.LayoutOptions;

namespace TokkDb.Assistant.Application.Shell;

/// <summary>
/// "What happened" (TR-5, UI-6, UI-8): the diagram of the request being looked at, drawn from
/// the layout the Diagram project computes, above the same layout as a focusable list - each
/// step's name, status and outcome in trace order - so that a screen reader has what the
/// drawing has. Selecting a block or a list item opens the same detail panel (TR-6). Both grow
/// while the request runs, and an earlier request's diagram comes back by choosing it.
/// </summary>
public sealed class StepsPane : Grid
{
    private readonly ChatViewModel _model;
    private readonly DiagramView _diagram;
    private readonly VerticalStackLayout _list;
    private readonly Label _title;
    private readonly VerticalStackLayout _detail;
    private Ulid? _selected;

    public StepsPane(ChatViewModel model)
    {
        _model = model;
        BackgroundColor = Theme.Window;
        RowDefinitions = [new RowDefinition(44), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)];

        _title = Theme.Text12("What happened", Theme.Bright, bold: true);
        var head = new Grid { Padding = new Thickness(16, 0), ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        head.Add(_title, 0, 0);
        _title.VerticalOptions = LayoutOptions.Center;
        this.Add(head, 0, 0);
        var rule = Theme.Rule();
        rule.VerticalOptions = LayoutOptions.End;
        this.Add(rule, 0, 0);

        _diagram = new DiagramView { Margin = new Thickness(8, 8, 8, 4) };
        _diagram.BlockSelected += block => MainThread.BeginInvokeOnMainThread(() => Select(block.StepId));

        _list = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(12, 4, 12, 12) };
        SemanticProperties.SetDescription(_list, "The steps of this request, in order");

        var body = new VerticalStackLayout { Spacing = 0 };
        body.Add(_diagram);
        body.Add(Theme.Rule());
        body.Add(_list);
        this.Add(new ScrollView { Content = body }, 0, 1);

        _detail = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(16, 12), BackgroundColor = Theme.Panel };
        _detail.IsVisible = false;
        this.Add(_detail, 0, 2);

        _model.Steps.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Render);
        _model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatViewModel.Watching)) MainThread.BeginInvokeOnMainThread(Render); };
        Render();
    }

    private void Render()
    {
        _list.Clear();
        var steps = _model.Steps.ToList();
        var calls = steps.Count(static step => step.IsModelCall);
        var took = steps.Where(static step => step.Step.Took is not null).Sum(static step => step.Step.Took!.Value.TotalSeconds);
        _title.Text = steps.Count == 0 ? "What happened" : $"What happened · {took:0.0}s · {calls} model call{(calls == 1 ? "" : "s")}";

        // One layout for both: the drawing above and the list below are the same blocks in the same order (UI-8).
        var changed = _model.Watching is { } watching ? _model.Recorder.Changes(watching).Where(static change => change.StepId is not null).Select(static change => change.StepId!.Value).ToHashSet() : [];
        var layout = DiagramLayouts.Layout(steps.Select(static item => item.Step).ToList(), stepsWithChanges: changed);
        _diagram.Show(layout, _selected);

        if (steps.Count == 0)
        {
            _list.Add(Theme.Text11("Nothing yet. Say something, and the steps appear here as they happen.", Theme.Dimmer));
            return;
        }

        var byId = steps.ToDictionary(static item => item.Step.Id);
        foreach (var entry in layout.Blocks)
        {
            var item = byId[entry.StepId];
            var row = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], Padding = new Thickness(10 + entry.Depth * 12, 8, 10, 8) };
            var name = Theme.Text12(item.Name, item.Step.Status is StepStatus.Failed or StepStatus.Interrupted ? Theme.Loss : Theme.Bright, bold: true);
            var status = Theme.Text11(item.Status, item.Step.Status switch
            {
                StepStatus.Running => Theme.Violet,
                StepStatus.Failed => Theme.Loss,
                StepStatus.Interrupted => Theme.Warn,
                _ => Theme.Dim
            });
            row.Add(name, 0, 0);
            row.Add(status, 1, 0);

            var block = new VerticalStackLayout { Spacing = 2 };
            block.Add(row);
            if (item.Outcome.Length > 0)
            {
                var outcome = Theme.Text11(item.Outcome.Length > 160 ? item.Outcome[..159] + "…" : item.Outcome, Theme.Dim);
                outcome.Margin = new Thickness(10, 0, 10, 6);
                block.Add(outcome);
            }

            var selected = _selected == item.Step.Id;
            var card = Theme.Card(block, selected ? Theme.Hover : Theme.Raised, selected ? Theme.Bright : item.IsModelCall ? Theme.DeepViolet : Theme.Border, new Thickness(2, 2));
            SemanticProperties.SetDescription(card, $"{item.Name}, {item.Status}. {item.Outcome}");
            var tap = new TapGestureRecognizer();
            var id = item.Step.Id;
            tap.Tapped += (_, _) => Select(id);
            card.GestureRecognizers.Add(tap);
            _list.Add(card);
        }
    }

    /// <summary>A block or a list item: the same selection, the same panel (UI-8).</summary>
    private void Select(Ulid stepId)
    {
        _selected = stepId;
        _diagram.Select(stepId);
        var item = _model.Steps.FirstOrDefault(step => step.Step.Id == stepId);
        if (item is null) return;
        ShowDetail(item);
        Render();
    }

    /// <summary>The detail panel (TR-6, EX-4): the registered view for what the step was, JSON otherwise.</summary>
    private void ShowDetail(StepItem item)
    {
        _detail.Clear();
        _detail.IsVisible = true;

        var head = new HorizontalStackLayout { Spacing = 8 };
        head.Add(Theme.Text12(item.Name, Theme.Bright, bold: true));
        head.Add(Theme.Text11($"{item.Step.StartedAt.ToLocalTime():HH:mm:ss}" + (item.Step.Took is { } took ? $" · {took.TotalSeconds:0.00}s" : "") + $" · {item.Status}", Theme.Dim));
        _detail.Add(head);

        var changes = _model.Watching is { } request
            ? _model.Recorder.Changes(request).Where(change => change.StepId == item.Step.Id).ToList()
            : [];

        try
        {
            _detail.Add(DetailViews.Render(new StepDetail(item.Step, changes, _model.Storage)));
        }
        catch (Exception failure)
        {
            _detail.Add(Theme.Text11("This could not be shown: " + failure.Message, Theme.Loss));
        }

        var close = new Label { Text = "close", FontFamily = Theme.Regular, FontSize = Theme.Tiny, TextColor = Theme.Violet };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { _detail.IsVisible = false; _selected = null; _diagram.Select(null); Render(); };
        close.GestureRecognizers.Add(tap);
        SemanticProperties.SetDescription(close, "Close the detail");
        _detail.Add(close);
    }
}
