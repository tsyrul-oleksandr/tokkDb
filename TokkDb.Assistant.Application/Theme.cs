namespace TokkDb.Assistant.Application;

/// <summary>
/// The look, lifted from the existing application rather than invented (docs/design): Open Sans,
/// 7px radii, 13px body with 12 and 11 secondary, 8px gaps; the palette by name so that no view
/// carries a hex of its own.
/// </summary>
public static class Theme
{
    public static readonly Color Window = Color.FromArgb("#111111");
    public static readonly Color Panel = Color.FromArgb("#202023");
    public static readonly Color Raised = Color.FromArgb("#252529");
    public static readonly Color Hover = Color.FromArgb("#303035");
    public static readonly Color Line = Color.FromArgb("#303035");
    public static readonly Color Border = Color.FromArgb("#38383D");
    public static readonly Color Text = Color.FromArgb("#CCCCCC");
    public static readonly Color Bright = Color.FromArgb("#FFFFFF");
    public static readonly Color Dim = Color.FromArgb("#888888");
    public static readonly Color Dimmer = Color.FromArgb("#666666");
    public static readonly Color Violet = Color.FromArgb("#A79BD8");
    public static readonly Color DeepViolet = Color.FromArgb("#6B5FA8");
    public static readonly Color Loss = Color.FromArgb("#E57373");
    public static readonly Color DeepLoss = Color.FromArgb("#C0392B");
    public static readonly Color Ok = Color.FromArgb("#7FB069");
    public static readonly Color Warn = Color.FromArgb("#E0B25A");

    public const string Regular = "OpenSansRegular";
    public const string Semibold = "OpenSansSemibold";

    public const double Body = 13;
    public const double Small = 12;
    public const double Tiny = 11;
    public const double Radius = 7;
    public const double Gap = 8;

    public static Label Text13(string text, Color? color = null) => new()
    {
        Text = text, FontFamily = Regular, FontSize = Body, TextColor = color ?? Text, LineBreakMode = LineBreakMode.WordWrap
    };

    public static Label Text12(string text, Color? color = null, bool bold = false) => new()
    {
        Text = text, FontFamily = bold ? Semibold : Regular, FontSize = Small, TextColor = color ?? Text, LineBreakMode = LineBreakMode.WordWrap
    };

    public static Label Text11(string text, Color? color = null) => new()
    {
        Text = text, FontFamily = Regular, FontSize = Tiny, TextColor = color ?? Dim, LineBreakMode = LineBreakMode.WordWrap
    };

    public static Border Card(View content, Color? background = null, Color? stroke = null, Thickness? padding = null) => new()
    {
        Content = content,
        BackgroundColor = background ?? Panel,
        Stroke = stroke ?? Border,
        StrokeThickness = 1,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(Radius) },
        Padding = padding ?? new Thickness(12, 10)
    };

    public static Button Action(string text, bool primary = false) => new()
    {
        Text = text,
        FontFamily = Semibold,
        FontSize = Small,
        TextColor = primary ? Bright : Text,
        BackgroundColor = primary ? DeepViolet : Hover,
        CornerRadius = (int)Radius,
        Padding = new Thickness(14, 6),
        MinimumHeightRequest = 30
    };

    public static BoxView Rule(bool vertical = false) => new()
    {
        Color = Line,
        WidthRequest = vertical ? 1 : -1,
        HeightRequest = vertical ? -1 : 1,
        HorizontalOptions = vertical ? LayoutOptions.Start : LayoutOptions.Fill,
        VerticalOptions = vertical ? LayoutOptions.Fill : LayoutOptions.Start
    };
}
