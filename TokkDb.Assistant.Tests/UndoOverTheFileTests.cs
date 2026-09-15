using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage.Engine;

namespace TokkDb.Assistant.Tests;

/// <summary>Found by the 9.7 run on Mac Catalyst: after S-1, S-2, S-5 and an import with rejected rows, "undo" failed over the engine with an index error.</summary>
public sealed class UndoOverTheFileTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("undo-file");

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task The_whole_sequence_of_the_app_run_can_be_undone_over_the_engine()
    {
        using var storage = new TokkDbStorage(_database.FilePath);
        var model = new ScriptedModel();
        using var services = new ServiceCollection().AddScriptedModel(model).AddAssistantAgents().AddSingleton(storage.Traces).AddSingleton(new ToolCatalog(storage)).BuildServiceProvider();
        var orchestrator = new Orchestrator(storage, storage.Traces, services.GetRequiredService<OperationRunner>(), new ToolCatalog(storage), new OrchestratorOptions { Clock = static () => AgentsHost.Today });

        model.Answer("intent", Scenarios.Intent("store")).Answer("extraction", Scenarios.ExtractedConferences);
        var s1 = await orchestrator.HandleAsync(new TurnInput(null, "I want to save these — the conference in Lviv on 14 March 2025, 12 000 hryvnia, and the one in Kyiv on 20 May 2025, 8 500."));
        Assert.True(s1.Succeeded, s1.Reply);
        var conversation = s1.ConversationId;

        model.Answer("mapping", Scenarios.MapOntoConferences);
        var s2 = await orchestrator.HandleAsync(new TurnInput(conversation, "Here are more of them", Files: [AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv)]));
        Assert.True(s2.Succeeded, s2.Reply);

        model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        var s3 = await orchestrator.HandleAsync(new TurnInput(conversation, "How much did I spend on conferences last year?"));
        Assert.True(s3.Succeeded, s3.Reply);

        model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv());
        var s5 = await orchestrator.HandleAsync(new TurnInput(conversation, "The Lviv one was actually 13 000."));
        Assert.True(s5.Succeeded, s5.Reply);

        model.Answer("mapping", Scenarios.MapOntoConferences);
        var bad = new System.Text.StringBuilder("name,city,date,cost,notes\n");
        var cities = new[] { "Lviv", "Kyiv", "Prague", "Vilnius", "Vienna" };
        for (var i = 0; i < 100; i++)
        {
            var date = i == 10 ? "the 14th of never" : $"2025-{i % 12 + 1:00}-{i % 27 + 1:00}";
            var cost = i == 40 ? "twelve thousand" : i == 77 ? "n/a" : (400 + i * 37).ToString();
            bad.Append($"Meetup {i + 1},{cities[i % 5]},{date},{cost},{(i % 3 == 0 ? "tickets" : "")}\n");
        }

        var n4 = await orchestrator.HandleAsync(new TurnInput(conversation, "And these", Files: [AgentsHost.Csv("conferences-bad", bad.ToString())]));
        Assert.True(n4.Succeeded, n4.Reply);
        Assert.Contains("3 could not be kept", n4.Reply);
        var before = storage.GetAll("conferences").Count;

        model.Answer("intent", Scenarios.Intent("undo"));
        var undone = await orchestrator.HandleAsync(new TurnInput(conversation, "undo"));

        Assert.True(undone.Succeeded, undone.Reply + " / " + undone.Failure);
        Assert.Equal(before - 97, storage.GetAll("conferences").Count);
    }
}
