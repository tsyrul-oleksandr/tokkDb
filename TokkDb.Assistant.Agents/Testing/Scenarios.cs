using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Testing;

/// <summary>
/// What the scenarios of §5 script the fake model with (D-12): the answers a real model would
/// give, for the operations each scenario makes. Nothing here decides anything - the decisions
/// are C#'s - and every answer is one the schema of its operation allows.
/// </summary>
public static class Scenarios
{
    public const string ConferencesCsv =
        """
        name,city,date,cost,notes
        EuroPython,Prague,2025-07-20,840,tickets and hotel
        DevDays,Vilnius,2025-05-12,600,flights 210
        PyCon Lviv,Lviv,2025-03-14,12000,
        KyivJS,Kyiv,2025-05-20,8500,late registration
        """;

    /// <summary>S-1: the two conferences the person typed, as the extraction answers them.</summary>
    public const string ExtractedConferences =
        """
        {"kind":"conferences","records":[
          {"fields":[{"name":"name","value":"Lviv conference","kind":"text"},{"name":"city","value":"Lviv","kind":"text"},{"name":"date","value":"2025-03-14","kind":"date"},{"name":"cost","value":"12000","kind":"number"}]},
          {"fields":[{"name":"name","value":"Kyiv conference","kind":"text"},{"name":"city","value":"Kyiv","kind":"text"},{"name":"date","value":"2025-05-01","kind":"date"},{"name":"cost","value":"8500","kind":"number"}]}
        ]}
        """;

    /// <summary>S-2: the spreadsheet onto the thing S-1 made, with the notes column new.</summary>
    public const string MapOntoConferences =
        """
        {"choice":"conferences","newName":"","purpose":"","fields":[{"incoming":"name","existing":"name"},{"incoming":"city","existing":"city"},{"incoming":"date","existing":"date"},{"incoming":"cost","existing":"cost"},{"incoming":"notes","existing":""}]}
        """;

    /// <summary>S-3: how much was spent on conferences last year, as a query with a total.</summary>
    public const string ConferencesLastYear =
        """
        {"thing":"conferences","where":[{"field":"date","operator":"at least","values":["2025-01-01"]},{"field":"date","operator":"before","values":["2026-01-01"]}],"orderBy":"","largestFirst":false,"limit":0,"total":"cost"}
        """;

    /// <summary>S-3's query as the storage runs it: conferences dated in 2025, with the cost totalled.</summary>
    public static StorageQuery LastYearQuery() =>
        new StorageQuery("conferences", [new QueryCondition("date", QueryOperator.GreaterOrEqual, new DateOnly(2025, 1, 1)), new QueryCondition("date", QueryOperator.LessThan, new DateOnly(2026, 1, 1))]);

    public static string Intent(string word) => $$"""{"intent":"{{word}}"}""";

    /// <summary>S-5: the correction, pointed at whichever shown record has "Lviv" in its name.</summary>
    public static Func<ScriptedRequest, string> CorrectLviv(string value = "13000") => request =>
    {
        var line = request.Content.Split('\n').FirstOrDefault(static line => line.Contains("Lviv", StringComparison.Ordinal) && line.Length > 2 && char.IsDigit(line[0]));
        var number = line is null ? 1 : int.Parse(line[..line.IndexOf('.')]);
        return $$"""{"which":{{number}},"field":"cost","value":"{{value}}"}""";
    };

    public const string DropNotes = """{"action":"remove-field","thing":"conferences","field":"notes","newName":"","kind":""}""";

    public static void GivenConferences(IStorage storage) =>
        storage.CreateCollection(new CollectionDefinition("conferences", "conferences I went to", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, "what it was called", required: true),
            new ColumnDefinition("city", ColumnType.Text, "where it was"),
            new ColumnDefinition("date", ColumnType.Date, "when"),
            new ColumnDefinition("cost", ColumnType.Decimal, "what it cost")
        ]));

    /// <summary>N-1's other candidate: the same shape as conferences, so that a file could plausibly be either.</summary>
    public static void GivenTrips(IStorage storage) =>
        storage.CreateCollection(new CollectionDefinition("trips", "journeys I made", columns:
        [
            new ColumnDefinition("name", ColumnType.Text, "what the trip was", required: true),
            new ColumnDefinition("city", ColumnType.Text, "which city"),
            new ColumnDefinition("date", ColumnType.Date, "when"),
            new ColumnDefinition("cost", ColumnType.Decimal, "what it cost")
        ]));
}
