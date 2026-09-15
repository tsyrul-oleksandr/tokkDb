using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;

namespace TokkDb.Assistant.Tests;

/// <summary>Found by the 9.7 run: "keep these as trips" went into conferences. A name the person gives is the placement, and costs no mapping call.</summary>
public sealed class NamedThingTests
{
    [Theory]
    [InlineData("Keep these as trips", "trips")]
    [InlineData("keep these as my conference expenses", "conference expenses")]
    [InlineData("Call them trips.", "trips")]
    [InlineData("These are trips, not conferences", "trips")]
    [InlineData("Put these under trips please", "trips")]
    [InlineData("Here are more of them", null)]
    [InlineData("as well as before", null)]
    public void The_thing_the_person_named_is_read(string said, string? expected) => Assert.Equal(expected, Intents.NamedThing(said));

    [Fact]
    public async Task A_file_kept_as_a_named_new_thing_makes_that_thing_without_a_mapping_call()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);

        var outcome = await host.Say(null, "Keep these as trips", AgentsHost.Csv("trips-2025", "name,city,date,cost,notes\nSpring trip,Kraków,2025-04-03,11750,by train\n"));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("trips"));
        Assert.Single(host.Storage.GetAll("trips"));
        Assert.Empty(host.Storage.GetAll("conferences"));
        Assert.Equal(0, host.Model.Calls("mapping"));
        Assert.Contains(host.Recorder.Read(outcome.RequestId)!.Value.Steps, static step => step.Name == "where it belongs" && step.Call is null);
    }

    [Fact]
    public async Task A_file_kept_as_an_existing_thing_goes_there_without_a_mapping_call()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);

        var outcome = await host.Say(null, "Keep these as conferences", AgentsHost.Csv("spring", "name,city,date,cost\nEuroPython,Prague,2025-07-20,840\n"));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Single(host.Storage.GetAll("conferences"));
        Assert.Equal(0, host.Model.Calls("mapping"));
    }
}
