using TokkDb.Assistant.Agents.Retention;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Step 9.2 (TR-7, TR-8, NF-4d): the diagnostics purge touches nothing but diagnostics, every
/// change stays attributable to a request and a time, compensation still works inside its window
/// and refuses as "no longer kept" outside it, and a configuration whose history purge would
/// reach inside the compensation window is refused naming both moments before anything is touched.
/// </summary>
public sealed class RetentionTests
{
    private static readonly RetentionWindows Windows = RetentionWindows.Default;

    [Fact]
    public async Task A_diagnostics_purge_touches_nothing_but_diagnostics_and_an_undo_still_works_inside_the_window()
    {
        using var host = new AgentsHost();
        var (conversation, stored) = await GivenConferencesStored(host);
        var turns = host.Storage.Conversations.Turns(conversation).Count;
        var recordsBefore = host.Storage.GetAll("conferences").Count;
        var changesBefore = host.Recorder.Changes(stored);
        Assert.NotEmpty(changesBefore);

        // Forty days on: past the diagnostics window, inside the compensation window.
        var now = DateTimeOffset.UtcNow + TimeSpan.FromDays(40);
        var report = RetentionSweep.Run(host.Storage, host.Recorder, Windows, now);

        Assert.Equal(1, report.RequestsPurged);
        Assert.Null(host.Recorder.Read(stored));

        // Untouched: the data, the conversation, the change journal with its request and its time.
        Assert.Equal(recordsBefore, host.Storage.GetAll("conferences").Count);
        Assert.Equal(turns, host.Storage.Conversations.Turns(conversation).Count);
        var changesAfter = host.Recorder.Changes(stored);
        Assert.Equal(changesBefore.Select(static change => change.Id), changesAfter.Select(static change => change.Id));
        Assert.All(changesAfter, change => { Assert.Equal(stored, change.RequestId); Assert.True(change.At > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)); });

        // Versions are still kept: the history window is the compensation window, and nothing old enough has passed it.
        Assert.Equal(0, report.VersionsPurged);
        Assert.All(changesAfter.Where(static change => change.IsRecordChange), change => Assert.True(host.Storage.Keeps(change.CollectionName, change.RecordId!.Value, change.VersionId!.Value)));

        // And the import can still be taken back, from the journal alone (TR-8).
        host.Model.Answer("intent", Scenarios.Intent("undo"));
        var undone = await host.Say(conversation, "undo");
        Assert.True(undone.Succeeded, undone.Reply);
        Assert.Empty(host.Storage.GetAll("conferences"));
    }

    [Fact]
    public async Task History_outside_the_window_is_purged_and_an_undo_then_refuses_as_no_longer_kept()
    {
        using var host = new AgentsHost();
        var (conversation, stored) = await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        await host.Say(conversation, "How much did I spend on conferences last year?");
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv());
        var first = await host.Say(conversation, "The Lviv one was actually 13 000.");
        Assert.True(first.Succeeded && first.Reply.Contains("to 13000", StringComparison.Ordinal), first.Reply);
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("14000"));
        var corrected = await host.Say(conversation, "The Lviv one was actually 14 000.");
        Assert.True(corrected.Succeeded && corrected.Reply.Contains("to 14000", StringComparison.Ordinal), corrected.Reply);
        var change = Assert.Single(host.Recorder.Changes(corrected.RequestId));

        // A hundred days on: the compensation window has closed, and with it the history.
        var now = DateTimeOffset.UtcNow + TimeSpan.FromDays(100);
        var report = RetentionSweep.Run(host.Storage, host.Recorder, Windows, now);

        Assert.True(report.VersionsPurged > 0, "no versions were purged");
        Assert.True(report.RecordsSwept >= 4, $"{report.RecordsSwept} records swept");
        Assert.False(host.Storage.Keeps("conferences", change.RecordId!.Value, change.PreviousVersionId!.Value));
        Assert.Equal(14000m, host.Storage.GetById("conferences", change.RecordId.Value)!["cost"]);

        host.Model.Answer("intent", Scenarios.Intent("undo"));
        var refused = await host.Say(conversation, "undo");
        Assert.True(refused.Succeeded, refused.Reply);
        Assert.Contains("no longer kept", refused.Reply);
        Assert.Equal(14000m, host.Storage.GetById("conferences", change.RecordId.Value)!["cost"]);
    }

    [Fact]
    public async Task A_configuration_that_would_purge_inside_the_compensation_window_is_refused_before_anything_is_touched()
    {
        using var host = new AgentsHost();
        var (_, stored) = await GivenConferencesStored(host);
        var wrong = Windows with { History = TimeSpan.FromDays(30), Compensation = TimeSpan.FromDays(90) };
        var now = DateTimeOffset.UtcNow + TimeSpan.FromDays(40);

        var refused = Assert.Throws<RetentionConflictException>(() => RetentionSweep.Run(host.Storage, host.Recorder, wrong, now));

        Assert.Equal(now - TimeSpan.FromDays(30), refused.PurgeHistoryBefore);
        Assert.Equal(now - TimeSpan.FromDays(90), refused.CompensationWindowStart);
        Assert.Contains(refused.PurgeHistoryBefore.ToString("O"), refused.Message);
        Assert.Contains(refused.CompensationWindowStart.ToString("O"), refused.Message);

        // Nothing was purged on the way to the refusal.
        Assert.NotNull(host.Recorder.Read(stored));
    }

    [Fact]
    public async Task Ending_the_window_early_for_one_request_purges_the_history_of_what_it_changed()
    {
        using var host = new AgentsHost();
        var (conversation, _) = await GivenConferencesStored(host);
        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        await host.Say(conversation, "How much did I spend on conferences last year?");
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv());
        var corrected = await host.Say(conversation, "The Lviv one was actually 13 000.");
        var change = Assert.Single(host.Recorder.Changes(corrected.RequestId));
        Assert.True(host.Storage.Keeps("conferences", change.RecordId!.Value, change.PreviousVersionId!.Value));

        var purged = RetentionSweep.EndWindow(host.Storage, host.Recorder, corrected.RequestId, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

        Assert.True(purged > 0);
        Assert.False(host.Storage.Keeps("conferences", change.RecordId.Value, change.PreviousVersionId.Value));
        Assert.Equal(13000m, host.Storage.GetById("conferences", change.RecordId.Value)!["cost"]);
    }

    private static async Task<(Ulid Conversation, Ulid Request)> GivenConferencesStored(AgentsHost host)
    {
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        var outcome = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));
        Assert.True(outcome.Succeeded, outcome.Reply);
        return (outcome.ConversationId, outcome.RequestId);
    }
}
