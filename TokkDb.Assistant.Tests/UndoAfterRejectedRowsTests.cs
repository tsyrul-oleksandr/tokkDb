using TokkDb.Assistant.Agents.Testing;

namespace TokkDb.Assistant.Tests;

/// <summary>Found by the 9.7 run: undoing an import that rejected some rows failed with an index error.</summary>
public sealed class UndoAfterRejectedRowsTests
{
    [Fact]
    public async Task An_import_with_rejected_rows_can_be_undone()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        const string csv = """
            name,city,date,cost,notes
            EuroPython,Prague,2025-07-20,840,tickets and hotel
            Broken,Nowhere,the 14th of never,600,
            DevDays,Vilnius,2025-05-12,600,flights 210
            Odd,Kyiv,2025-05-20,twelve thousand,
            """;
        var stored = await host.Say(null, "", AgentsHost.Csv("conferences-bad", csv));
        Assert.True(stored.Succeeded, stored.Reply);
        Assert.Contains("2 could not be kept", stored.Reply);
        Assert.Equal(2, host.Storage.GetAll("conferences").Count);

        host.Model.Answer("intent", Scenarios.Intent("undo"));
        var undone = await host.Say(stored.ConversationId, "undo");

        Assert.True(undone.Succeeded, undone.Reply);
        Assert.Empty(host.Storage.GetAll("conferences"));
    }
}
