using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Found on 2026-09-15: an extraction whose answer ran past the output cap was sent back for
/// repair twice and failed as "not JSON", which named the wrong thing. A cut-off answer fails at
/// once, says what the cap was, and is counted as its own mode (R-2b, AG-1e).
/// </summary>
public sealed class CutOffTests
{
    [Fact]
    public async Task An_answer_cut_off_at_the_output_cap_fails_at_once_and_says_so()
    {
        using var host = new AgentsHost();
        host.Model.Answer("intent", Scenarios.Intent("store"))
            .CutOff("extraction", """{"kind":"conferences","records":[{"fields":[{"name":"name","value":"Lviv conference","kind":"text"},{"name":"city","value":"Lviv","kind""");

        var outcome = await host.Say(null, "I want to save these — the conference in Lviv on 14 March 2025, 12 000 hryvnia.");

        Assert.False(outcome.Succeeded);
        Assert.Contains("cut off", outcome.Reply);
        Assert.Contains("Nothing was changed", outcome.Reply);
        Assert.Equal(1, host.Model.Calls("extraction"));
        Assert.Empty(host.Storage.GetCollectionDefinitions());

        var step = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "what is in the text");
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.StartsWith("cut off at the output cap", step.Output);
        Assert.Equal(0, step.Call!.Retries);
    }

    [Fact]
    public void A_reply_knows_when_it_was_cut_off()
    {
        Assert.True(new ModelReply("{", 1, 1, 1, 2, TimeSpan.Zero, "length").WasCutOff);
        Assert.False(new ModelReply("{}", 1, 1, 1, 2, TimeSpan.Zero, "stop").WasCutOff);
        Assert.False(new ModelReply("{}", 1, 1, 1, 2, TimeSpan.Zero, null).WasCutOff);
    }
}
