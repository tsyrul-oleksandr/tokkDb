using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The correction path (9.1, QR-4, QR-4a, QR-4b): a positional reference resolves against the
/// identities the handle recorded, never a re-run; staleness is checked at the field being
/// corrected and nowhere else. The three cases the requirement names are each a test, and so is
/// the record that was deleted between the answer and the correction.
/// </summary>
public sealed class CorrectionTests
{
    /// <summary>An unrelated edit meanwhile does not block the correction.</summary>
    [Fact]
    public async Task Correcting_a_field_after_an_unrelated_field_was_edited_succeeds()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        // Someone edits the city while the results are on screen.
        host.Storage.Update(lviv.With("city", "Lemberg"));

        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("13000"));
        var outcome = await host.Say(conversation, "The Lviv one was actually 13 000.");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("from 12000 to 13000", outcome.Reply);
        var now = host.Storage.GetById("conferences", lviv.Id)!;
        Assert.Equal(13000m, now["cost"]);
        Assert.Equal("Lemberg", now["city"]);
        Assert.Single(host.Recorder.Changes(outcome.RequestId));
    }

    /// <summary>An edit to the very field meanwhile stops the correction and shows both values.</summary>
    [Fact]
    public async Task Correcting_a_field_that_was_edited_meanwhile_stops_and_shows_both_values()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        host.Storage.Update(lviv.With("cost", 12500m));

        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("13000"));
        var outcome = await host.Say(conversation, "The Lviv one was actually 13 000.");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("has changed since you saw it", outcome.Reply);
        Assert.Contains("12000", outcome.Reply);
        Assert.Contains("12500", outcome.Reply);
        Assert.Contains("Nothing was changed", outcome.Reply);
        Assert.Equal(12500m, host.Storage.GetById("conferences", lviv.Id)!["cost"]);
        Assert.Empty(host.Recorder.Changes(outcome.RequestId));

        var check = host.Recorder.Read(outcome.RequestId)!.Value.Steps.Single(static step => step.Name == "the check");
        Assert.Contains("12000 then, 12500 now", check.Output);
    }

    /// <summary>A record that has left the query's filter is still correctable: the handle, not a re-run, says what was pointed at.</summary>
    [Fact]
    public async Task A_record_that_no_longer_matches_the_filter_is_still_correctable_by_position()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        // The query was "last year"; the record moves to another year, so a re-run would not find it.
        host.Storage.Update(lviv.With("date", new DateOnly(2023, 3, 14)));
        var rerun = host.Storage.ExecuteQuery(Scenarios.LastYearQuery());
        Assert.DoesNotContain(rerun.Records, record => record.Id == lviv.Id);

        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("13000"));
        var outcome = await host.Say(conversation, "The Lviv one was actually 13 000.");

        Assert.True(outcome.Succeeded, outcome.Reply);
        Assert.Contains("from 12000 to 13000", outcome.Reply);
        Assert.Equal(13000m, host.Storage.GetById("conferences", lviv.Id)!["cost"]);
    }

    /// <summary>QR-4a: a record inserted meanwhile does not change which record "the third one" means, and a deleted one says so.</summary>
    [Fact]
    public async Task A_positional_reference_resolves_against_the_handle_and_a_deleted_record_says_so()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var lviv = host.Storage.GetAll("conferences").Single(static record => record["name"] is "PyCon Lviv");

        // Inserted between the answer and the correction: the positions the person saw do not move.
        host.Storage.Create("conferences", new Dictionary<string, object?> { ["name"] = "AAA First", ["city"] = "Aarhus", ["date"] = new DateOnly(2025, 1, 2), ["cost"] = 1m });

        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("13000"));
        var corrected = await host.Say(conversation, "The Lviv one was actually 13 000.");
        Assert.True(corrected.Succeeded, corrected.Reply);
        Assert.Equal(13000m, host.Storage.GetById("conferences", lviv.Id)!["cost"]);

        // Deleted since: the message says so rather than correcting another record.
        host.Storage.Delete("conferences", lviv.Id);
        host.Model.Answer("intent", Scenarios.Intent("correct")).Answer("correction", Scenarios.CorrectLviv("14000"));
        var gone = await host.Say(conversation, "The Lviv one was actually 14 000.");

        Assert.True(gone.Succeeded, gone.Reply);
        Assert.Contains("has since been removed, so nothing was changed", gone.Reply);
        Assert.All(host.Storage.GetAll("conferences"), record => Assert.NotEqual(14000m, record["cost"]));
    }

    private static async Task<Ulid> GivenConferencesShown(AgentsHost host)
    {
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        var stored = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));
        Assert.True(stored.Succeeded, stored.Reply);

        host.Model.Answer("intent", Scenarios.Intent("find")).Answer("query", Scenarios.ConferencesLastYear);
        var shown = await host.Say(stored.ConversationId, "How much did I spend on conferences last year?");
        Assert.True(shown.Succeeded, shown.Reply);
        return stored.ConversationId;
    }
}
