using System.ComponentModel;

namespace TokkDb.LLM.Core;

/// <summary>
/// Presentation command issued by an agent: "render these records in the chat".
///
/// It is not a query. The agent decides <em>which</em> records to show; the
/// application decides <em>how</em> they are rendered.
/// </summary>
public sealed record ShowRecordsRequest(
    [property: Description("Collection the records belong to.")]
    string CollectionName,

    [property: Description(
        "Ids of the records to display, in the order they should appear. Take these from a previous query result; " +
        "an empty list renders an empty state.")]
    IReadOnlyList<string> RecordIds,

    [property: Description(
        "Optional column names to show beside each record's display value, such as a price or a status. " +
        "Only columns of this collection are accepted.")]
    IReadOnlyList<string>? AdditionalFields = null);

/// <summary>
/// One additional column value shown next to the display value. Already
/// formatted by the application - never by the model.
/// </summary>
public sealed record RecordDisplayField(string Name, string Value);

/// <summary>
/// A single record prepared for display.
/// <see cref="DisplayValue"/> always comes from the collection's DisplayRule
/// (or its deterministic fallback), never from the model.
/// </summary>
public sealed record RecordDisplayItem(
    string RecordId,
    string CollectionName,
    string DisplayValue,
    IReadOnlyList<RecordDisplayField> AdditionalFields);

/// <summary>
/// Structured chat content describing a record list. This is the strongly typed
/// alternative to parsing assistant text, and it contains no provider or agent
/// framework types, so it works identically for OpenAI, Ollama and any future
/// provider.
/// </summary>
public sealed record RecordsDisplayMessage(
    string CollectionName,
    IReadOnlyList<RecordDisplayItem> Records,
    IReadOnlyList<string> RequestedAdditionalFields,
    int RequestedRecordCount,
    IReadOnlyList<string> UnresolvedRecordIds,
    IReadOnlyList<string> InvalidAdditionalFields)
{
    public bool IsEmpty => Records.Count == 0;

    public static RecordsDisplayMessage Empty(string collectionName) =>
        new(
            collectionName,
            Array.Empty<RecordDisplayItem>(),
            Array.Empty<string>(),
            0,
            Array.Empty<string>(),
            Array.Empty<string>());
}

public sealed class RecordsDisplayEventArgs : EventArgs
{
    public RecordsDisplayEventArgs(RecordsDisplayMessage message)
    {
        Message = message;
    }

    public RecordsDisplayMessage Message { get; }
}

/// <summary>
/// Application-level request to open one record on the Database page.
/// </summary>
public sealed record OpenRecordRequest(string CollectionName, string RecordId);

/// <summary>
/// Navigation abstraction between the chat surface and the Database page.
///
/// The chat raises a request; interested parts of the application (the shell,
/// which switches page, and the Database view model, which selects the record)
/// react to it. This is what keeps the chat from referencing the Database view
/// model directly.
/// </summary>
public interface IRecordNavigationService
{
    event EventHandler<OpenRecordRequest>? RecordNavigationRequested;

    void OpenRecord(OpenRecordRequest request);
}
