using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Agents.Testing;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Naming a thing when the file's own name will not do (IN-10, step 10.2): the model names it
/// from the fields and examples, C# checks the name before use and makes the file's name usable
/// when the model cannot; a name the person gives that will not do is refused with the reason.
/// </summary>
public sealed class NamingTests
{
    private const string Campaigns = "Customer,Email,Date,Subscription,Country\nJohn Doe,john@example.com,2026-08-28,Windows course,Ukraine\nJane Roe,jane@example.com,2026-08-29,Excel course,Poland\n";

    [Fact]
    public async Task A_file_whose_name_cannot_be_a_things_name_is_named_by_the_model()
    {
        using var host = new AgentsHost();
        host.Model.Answer("naming", """{"name":"custom campaigns"}""");

        var outcome = await host.Say(null, "", AgentsHost.Csv("133804_custom_campaigns_2", Campaigns));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.StartsWith("Started keeping custom campaigns and kept 2 of them", outcome.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("custom_campaigns"));
        Assert.Equal(1, host.Model.Calls("naming"));
        Assert.Contains("incoming: 133804_custom_campaigns_2", host.Model.Requests.Last().Content);

        var read = host.Recorder.Read(outcome.RequestId);
        Assert.NotNull(read);
        Assert.Contains(read.Value.Steps, static step => step.Name == "what to call it" && step.Call is not null);
    }

    [Fact]
    public async Task A_name_the_model_gives_that_will_not_do_falls_back_to_the_files_name_made_usable()
    {
        using var host = new AgentsHost();
        host.Model.Answer("naming", """{"name":"2026 campaigns"}""");

        var outcome = await host.Say(null, "", AgentsHost.Csv("133804_custom_campaigns_2", Campaigns));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("custom_campaigns_2"));
        Assert.Single(host.Storage.GetCollectionDefinitions());
        Assert.Contains(host.Recorder.Read(outcome.RequestId)!.Value.Steps, static step => step.Name == "what to call it" && (step.Output ?? "").Contains("not usable as a name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_model_cannot_name_it_the_rows_are_still_kept()
    {
        using var host = new AgentsHost();
        host.Model.Empty("naming");

        var outcome = await host.Say(null, "", AgentsHost.Csv("133804_custom_campaigns_2", Campaigns));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.StartsWith("Started keeping custom campaigns 2 and kept 2 of them", outcome.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("custom_campaigns_2"));
    }

    [Fact]
    public async Task A_usable_file_name_needs_no_call()
    {
        using var host = new AgentsHost();

        var outcome = await host.Say(null, "", AgentsHost.Csv("campaigns-2026", Campaigns));

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.NotNull(host.Storage.GetCollectionDefinition("campaigns_2026"));
        Assert.Equal(0, host.Model.Calls("naming"));
    }

    [Fact]
    public async Task A_name_the_person_gives_that_will_not_do_is_refused_with_the_reason()
    {
        using var host = new AgentsHost();

        var outcome = await host.Say(null, "Keep these as 9lives", AgentsHost.Csv("133804_custom_campaigns_2", Campaigns));

        Assert.False(outcome.Succeeded);
        Assert.Contains("'9lives' cannot be a name for a thing", outcome.Reply);
        Assert.Empty(host.Storage.GetCollectionDefinitions());
        Assert.Equal(0, host.Model.Calls("naming"));
    }

    [Fact]
    public void The_check_and_the_fallback_follow_the_storages_rule()
    {
        Assert.True(IncomingShape.IsUsableThingName("custom_campaigns_2"));
        Assert.False(IncomingShape.IsUsableThingName("133804_custom_campaigns_2"));
        Assert.False(IncomingShape.IsUsableThingName("_campaigns"));
        Assert.False(IncomingShape.IsUsableThingName(""));
        Assert.False(IncomingShape.IsUsableThingName(new string('a', 65)));

        Assert.Equal("custom_campaigns_2", IncomingShape.UsableFrom("133804_custom_campaigns_2"));
        Assert.Equal("trips", IncomingShape.UsableFrom("2025_trips"));
        Assert.Equal("things", IncomingShape.UsableFrom("2025"));
        Assert.Equal("things", IncomingShape.UsableFrom(""));
    }
}
