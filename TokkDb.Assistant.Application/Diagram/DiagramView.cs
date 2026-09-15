using Microsoft.Maui.Graphics;
using TokkDb.Assistant.Diagram;
using TokkDb.Assistant.Trace;
using Font = Microsoft.Maui.Graphics.Font;
using Measures = TokkDb.Assistant.Diagram.LayoutOptions;

namespace TokkDb.Assistant.App.Diagram;

/// <summary>
/// The diagram, drawn (D-9, TR-5, TR-5a): a GraphicsView over the layout the Diagram project
/// computed in points. One transform is produced from the view's width at draw time and kept;
/// the drawing goes through it and so does every click, so a block is hit where it was drawn
/// whatever the display's scale - and a display or size change invalidates, which makes a new
/// transform, never a new layout.
/// </summary>
public sealed class DiagramView : GraphicsView, IDrawable
{
    private DiagramLayout _layout = DiagramLayout.Empty;
    private DiagramTransform _transform = DiagramTransform.Identity;
    private Ulid? _selected;

    public DiagramView()
    {
        Drawable = this;
        BackgroundColor = Theme.Window;
        HeightRequest = 96;
        StartInteraction += OnTouch;
        SizeChanged += (_, _) => Invalidate();
        DeviceDisplay.Current.MainDisplayInfoChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Invalidate);
        SemanticProperties.SetDescription(this, "What happened, drawn as a sequence; the list beside it says the same");
    }

    /// <summary>A block was chosen with the pointer.</summary>
    public event Action<Block>? BlockSelected;

    /// <summary>The transform the last drawing used, which is the one a click goes through.</summary>
    public DiagramTransform Transform => _transform;

    public DiagramLayout Layout => _layout;

    public void Show(DiagramLayout layout, Ulid? selected = null)
    {
        _layout = layout;
        _selected = selected;

        // Tall enough before the first drawing, from the width the view has or a usual one, so
        // that a stack gives it room; the drawing then settles the height from its own transform.
        var width = Width > 0 ? Width : 540;
        HeightRequest = Math.Max(96, layout.Height * DiagramTransform.FitWidth(layout, width).Scale);
        Invalidate();
    }

    public void Select(Ulid? stepId)
    {
        _selected = stepId;
        Invalidate();
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        _transform = DiagramTransform.FitWidth(_layout, dirtyRect.Width);
        var wanted = Math.Max(96, _layout.Height * _transform.Scale);
        if (Math.Abs(HeightRequest - wanted) > 0.5) MainThread.BeginInvokeOnMainThread(() => HeightRequest = wanted);

        canvas.SaveState();
        canvas.Translate((float)_transform.OffsetX, (float)_transform.OffsetY);
        canvas.Scale((float)_transform.Scale, (float)_transform.Scale);
        canvas.Font = Font.Default;
        canvas.FontSize = 11;

        DrawLanes(canvas);
        foreach (var arrow in _layout.Arrows) DrawArrow(canvas, arrow);
        foreach (var block in _layout.Blocks.OrderBy(static block => block.Depth)) DrawBlock(canvas, block);

        canvas.RestoreState();
    }

    private void DrawLanes(ICanvas canvas)
    {
        var options = Measures.Default;
        foreach (var lane in _layout.Lanes)
        {
            var box = new RectF((float)(lane.X - options.LaneWidth / 2 + 6), 8, (float)(options.LaneWidth - 12), 28);
            canvas.FillColor = Theme.Panel;
            canvas.FillRoundedRectangle(box, 6);
            canvas.StrokeColor = Theme.Border;
            canvas.StrokeSize = 1;
            canvas.DrawRoundedRectangle(box, 6);
            canvas.FontColor = Theme.Bright;
            canvas.FontSize = 12;
            canvas.DrawString(lane.Title, box, HorizontalAlignment.Center, VerticalAlignment.Center);

            canvas.StrokeColor = Theme.Line;
            canvas.StrokeSize = 1;
            canvas.StrokeDashPattern = [3, 4];
            canvas.DrawLine((float)lane.X, 40, (float)lane.X, (float)Math.Max(48, _layout.Height - 4));
            canvas.StrokeDashPattern = null;
        }
    }

    private void DrawArrow(ICanvas canvas, Arrow arrow)
    {
        var y = (float)arrow.Y;
        var from = (float)arrow.FromX;
        var to = (float)arrow.ToX;
        canvas.StrokeColor = arrow.IsReturn ? Theme.Dimmer : Theme.Dim;
        canvas.StrokeSize = 1;
        canvas.StrokeDashPattern = arrow.IsReturn ? [4, 3] : null;
        canvas.DrawLine(from, y, to, y);
        canvas.StrokeDashPattern = null;

        // The head, pointing the way the arrow goes.
        var direction = to > from ? 1 : -1;
        var path = new PathF();
        path.MoveTo(to, y);
        path.LineTo(to - direction * 7, y - 4);
        path.LineTo(to - direction * 7, y + 4);
        path.Close();
        canvas.FillColor = arrow.IsReturn ? Theme.Dimmer : Theme.Dim;
        canvas.FillPath(path);
    }

    private void DrawBlock(ICanvas canvas, Block block)
    {
        var rect = new RectF((float)block.X, (float)block.Y, (float)block.Width, (float)block.Height);
        var selected = _selected == block.StepId;

        canvas.FillColor = block.Lane switch
        {
            Participant.Person => Theme.Hover,
            Participant.Model => Theme.DeepViolet.WithAlpha(0.55f),
            Participant.Storage => Theme.Ok.WithAlpha(0.28f),
            _ => Theme.Raised
        };
        canvas.FillRoundedRectangle(rect, 6);

        canvas.StrokeColor = block.Status switch
        {
            StepStatus.Failed => Theme.Loss,
            StepStatus.Interrupted => Theme.Warn,
            StepStatus.Running => Theme.Violet,
            _ => selected ? Theme.Bright : Theme.Border
        };
        canvas.StrokeSize = selected ? 2 : 1;
        canvas.DrawRoundedRectangle(rect, 6);

        canvas.FontColor = Theme.Bright;
        canvas.FontSize = 11;
        var name = block.Took is { } took && took.TotalMilliseconds >= 50 ? $"{block.Name} · {took.TotalSeconds:0.0}s" : block.Name;
        canvas.DrawString(name, new RectF(rect.X + 6, rect.Y + 2, rect.Width - 12, 16), HorizontalAlignment.Left, VerticalAlignment.Center);

        if (block.Summary is { } summary && rect.Height >= 30)
        {
            canvas.FontColor = Theme.Dim;
            canvas.FontSize = 9;
            canvas.DrawString(summary, new RectF(rect.X + 6, rect.Y + 17, rect.Width - 12, 12), HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    private void OnTouch(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        var point = e.Touches[0];
        var block = HitTesting.BlockAt(_layout, _transform, point.X, point.Y);
        if (block is null) return;

        _selected = block.StepId;
        Invalidate();
        BlockSelected?.Invoke(block);
    }
}
