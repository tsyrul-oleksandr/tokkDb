using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Changes;

/// <summary>
/// The question put to a person before something is lost or changed in meaning (UI-4, D-7, D-14,
/// AG-11d, NF-4d): the loss in counts and examples, never in schema terms; whether it can be
/// undone, said here while they can still decline; and how long a removed record stays
/// recoverable. The same card, with AG-11b's evidence, is what a partial undo is taken from.
///
/// Every string on it is first-tier vocabulary (UI-2): no collection, column, index, schema or
/// query, because this is the conversation.
/// </summary>
/// <param name="Title">What is being asked, as one line: "Drop the notes from conferences?"</param>
/// <param name="Lines">What would happen, one sentence each, with counts.</param>
/// <param name="Examples">A few of the things affected, by the name a person knows them by.</param>
/// <param name="CanBeUndone">Whether, having said yes, they could get it back.</param>
/// <param name="UndoNote">"This cannot be undone." or how long it stays recoverable, or null when nothing is lost.</param>
/// <param name="Yes">The words on the button that goes ahead.</param>
/// <param name="No">The words on the button that does not.</param>
public sealed record ConfirmationCard(
    string Title,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> Examples,
    bool CanBeUndone,
    string? UndoNote,
    string Yes = "Go ahead",
    string No = "Leave it as it is")
{
    public const string CannotBeUndone = "This cannot be undone.";

    /// <summary>The card for a set of classified changes, the ones that need asking first.</summary>
    public static ConfirmationCard For(IReadOnlyList<ClassifiedChange> changes, TimeSpan compensationWindow, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var asked = changes.Where(static change => change.NeedsConfirmation).ToList();
        if (asked.Count == 0) asked = [.. changes];

        var lines = new List<string>();
        var examples = new List<string>();
        var reversibility = Reversibility.Reversible;

        foreach (var change in asked)
        {
            lines.Add(Capitalise(change.Description) + ": " + change.Evidence.Sentence + ".");
            foreach (var example in change.Evidence.Examples)
            {
                if (examples.Count < ChangeClassifier.ExamplesShown * 2 && !examples.Contains(example)) examples.Add(example);
            }

            if (change.Reversibility > reversibility) reversibility = change.Reversibility;
        }

        var destructive = asked.Any(static change => change.Class is ChangeClass.Destructive);
        var recordsGo = asked.Any(static change => change.Action is DeleteRecords);
        var erased = asked.Any(static change => change.Action is EraseRecords);

        var note = reversibility switch
        {
            Reversibility.NotReversible when erased => CannotBeUndone + " Nothing of it stays in what is stored; the conversations that mention it are yours and are kept.",
            Reversibility.NotReversible => CannotBeUndone,
            _ when recordsGo => $"What is removed can be put back for {Days(compensationWindow)}, then it is gone for good.",
            _ when destructive => $"This can be undone for {Days(compensationWindow)}, as long as nothing else has changed these since.",
            _ => "This can be undone."
        };

        return new ConfirmationCard(
            title ?? Capitalise(asked[0].Description) + "?",
            lines,
            examples,
            reversibility is not Reversibility.NotReversible,
            note);
    }

    private static string Days(TimeSpan window) =>
        window.TotalDays >= 2 ? $"{(int)window.TotalDays} days" : window.TotalDays >= 1 ? "one day" : $"{(int)window.TotalHours} hours";

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
