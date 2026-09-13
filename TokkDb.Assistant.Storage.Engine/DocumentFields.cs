using TokkDb.Documents;
using TokkDb.Documents.Values;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// Reading and writing the fields of a system document, in one place.
///
/// The engine stores a document and interprets none of it, so every caller that keeps something
/// in a reserved collection has the same small job: a value that is absent has to read as absent
/// rather than throw, a moment has to come back as the UTC moment it went in as, and an
/// enumeration written as a word has to survive a member being added in front of it. Two files
/// doing that slightly differently is how a conversation and a trace end up disagreeing about
/// what an empty string means.
///
/// <b>Words rather than numbers for enumerations.</b> A stored <c>2</c> means whatever the second
/// member happens to be on the day it is read; a stored <c>WaitingForUser</c> means what it says,
/// and reordering the enumeration cannot quietly change what a year of traces recorded.
/// </summary>
internal static class DocumentFields
{
    public static ObjectDocumentValue Of(ObjectDocument document) => (ObjectDocumentValue)document.Value;

    public static string Text(ObjectDocumentValue value, string name) =>
        value.Values.GetValueOrDefault(name) is StringDocumentValue text ? text.Value : string.Empty;

    /// <summary>Absent and empty are the same thing here: both mean nobody wrote one.</summary>
    public static string? OptionalText(ObjectDocumentValue value, string name) =>
        Text(value, name) is { Length: > 0 } text ? text : null;

    public static Ulid? Identity(ObjectDocumentValue value, string name) =>
        value.Values.GetValueOrDefault(name) is UlidDocumentValue id ? id.Value : null;

    public static DateTimeOffset Moment(ObjectDocumentValue value, string name) =>
        OptionalMoment(value, name) ?? default;

    public static DateTimeOffset? OptionalMoment(ObjectDocumentValue value, string name) =>
        value.Values.GetValueOrDefault(name) is DateTimeDocumentValue moment
            ? new DateTimeOffset(DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc))
            : null;

    public static int Number(ObjectDocumentValue value, string name) =>
        value.Values.GetValueOrDefault(name) switch
        {
            IntDocumentValue number => number.Value,
            LongDocumentValue number => (int)number.Value,
            _ => 0
        };

    public static long Ticks(ObjectDocumentValue value, string name) =>
        value.Values.GetValueOrDefault(name) is LongDocumentValue number ? number.Value : 0;

    public static TEnum Word<TEnum>(ObjectDocumentValue value, string name, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Text(value, name), out var parsed) ? parsed : fallback;

    public static IDocumentValue Value(string? text) =>
        text is null ? new NullDocumentValue() : new StringDocumentValue(text);

    public static IDocumentValue Value(Ulid? id) =>
        id is { } identity ? new UlidDocumentValue(identity) : new NullDocumentValue();

    public static IDocumentValue Value(DateTimeOffset? moment) =>
        moment is { } at ? new DateTimeDocumentValue(at.UtcDateTime) : new NullDocumentValue();

    public static IDocumentValue Word<TEnum>(TEnum value) where TEnum : struct, Enum =>
        new StringDocumentValue(value.ToString()!);
}
