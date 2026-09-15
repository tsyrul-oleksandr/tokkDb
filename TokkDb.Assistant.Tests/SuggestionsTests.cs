using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Agents.Testing;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Suggestions for the composer (UI-9, step 10.1): the model phrases, C# decides what may be
/// shown - plain words, no duplicates, three to seven - and writes the options itself when the
/// model cannot; the call is traced and budgeted like any other and adds nothing to the conversation.
/// </summary>
public sealed class SuggestionsTests
{
    private const string Five = """{"suggestions":["Show my conferences from this year","Which of those was the most expensive?","The Lviv one was actually 13 000","Remove the Kyiv one","Run a query on the conferences collection","Show my conferences from this year"]}""";

    [Fact]
    public async Task The_models_options_are_shown_cleaned_and_nothing_is_added_to_the_conversation()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        var turns = host.Storage.Conversations.Turns(conversation).Count;
        host.Model.Answer("suggestions", Five);

        var set = await host.Orchestrator.SuggestAsync(conversation, draft: null);

        Assert.True(set.FromModel);
        Assert.Equal(["Show my conferences from this year", "Which of those was the most expensive?", "The Lviv one was actually 13 000", "Remove the Kyiv one"], set.Options);
        Assert.InRange(set.Options.Count, SuggestionSet.Fewest, SuggestionSet.Most);
        Assert.Equal(turns, host.Storage.Conversations.Turns(conversation).Count);

        // Traced and budgeted like any other call (TR-3, AG-1): one request of its own, with the call's figures.
        var read = host.Recorder.Read(set.RequestId);
        Assert.NotNull(read);
        Assert.Equal("suggestions", read.Value.Request.Operation);
        var step = Assert.Single(read.Value.Steps);
        Assert.Equal("what to say next", step.Name);
        Assert.NotNull(step.Call);
        Assert.True(step.Call.PromptTokens > 0 && step.Call.PromptTokens <= Operations.Suggestions.ContextBudget);
        Assert.Equal(1, host.Model.Calls("suggestions"));

        // What the model read: what is stored, the recent turns, what was typed.
        var content = host.Model.Requests.Last().Content;
        Assert.Contains("stored: conferences (name, city, date, cost, notes)", content);
        Assert.Contains("shown last (conferences): ", content);
        Assert.Contains("person: How much did I spend on conferences last year?", content);
        Assert.Contains("typed so far: nothing", content);
    }

    [Fact]
    public async Task A_draft_is_given_to_the_model_and_leads_the_fallback()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        host.Model.Answer("mapping", Scenarios.MapOntoConferences);
        var stored = await host.Say(null, "", AgentsHost.Csv("conferences-2025", Scenarios.ConferencesCsv));
        Assert.True(stored.Succeeded, stored.Reply);
        host.Model.Answer("suggestions", """{"suggestions":["Show my conferences from this year","Show my conferences in Lviv","Show my conferences that cost over 5000"]}""");

        var set = await host.Orchestrator.SuggestAsync(stored.ConversationId, "Show my");

        // Nothing was shown, so the model is given a few titles of what was lately kept, never rows.
        Assert.Contains("typed so far: Show my", host.Model.Requests.Last().Content);
        Assert.Contains("kept lately (conferences): ", host.Model.Requests.Last().Content);
        Assert.DoesNotContain("shown last", host.Model.Requests.Last().Content);
        Assert.All(set.Options.Take(3), option => Assert.StartsWith("Show my", option));

        host.Model.Empty("suggestions");
        var fallback = await host.Orchestrator.SuggestAsync(stored.ConversationId, "Show my");
        Assert.False(fallback.FromModel);
        Assert.StartsWith("Show my conferences", fallback.Options[0]);
    }

    [Fact]
    public async Task When_the_model_fails_the_options_are_grounded_in_what_is_stored()
    {
        using var host = new AgentsHost();
        var conversation = await GivenConferencesShown(host);
        host.Model.Empty("suggestions");

        var set = await host.Orchestrator.SuggestAsync(conversation, null);

        Assert.False(set.FromModel);
        Assert.InRange(set.Options.Count, SuggestionSet.Fewest, SuggestionSet.Most);
        Assert.Contains(set.Options, static option => option.Contains("largest cost", StringComparison.Ordinal));
        Assert.Contains(set.Options, static option => option.StartsWith("Remove ", StringComparison.Ordinal));
        Assert.Contains(set.Options, static option => option.Contains("conferences", StringComparison.Ordinal));
        Assert.Contains("Take back the last change", set.Options);
        Assert.All(set.Options, option => Assert.DoesNotContain(Suggestions.ForbiddenWords, word => option.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Trace.RequestState.Failed, host.Recorder.Request(set.RequestId)!.State);
    }

    [Fact]
    public async Task With_nothing_stored_and_nothing_said_the_options_show_how_to_begin_without_a_call()
    {
        using var host = new AgentsHost();

        var set = await host.Orchestrator.SuggestAsync(null, null);

        // Nothing for the model to read: C#'s own starters, and no call, no request.
        Assert.False(set.FromModel);
        Assert.InRange(set.Options.Count, SuggestionSet.Fewest, SuggestionSet.Most);
        Assert.Contains(set.Options, static option => option.StartsWith("Keep this", StringComparison.Ordinal));
        Assert.Contains("What can you keep for me?", set.Options);
        Assert.Equal(0, host.Model.Calls("suggestions"));
        Assert.Equal(Ulid.Empty, set.RequestId);

        // Something typed is something to read: the model is asked, and told that nothing is stored.
        host.Model.Answer("suggestions", """{"suggestions":["Keep this: my trip to Lviv on 3 May 2025, 1 200 hryvnia","Keep these as trips: …","What can you keep for me?"]}""");
        var typed = await host.Orchestrator.SuggestAsync(null, "Keep");

        Assert.True(typed.FromModel);
        Assert.Equal(1, host.Model.Calls("suggestions"));
        Assert.Contains("stored: nothing yet", host.Model.Requests.Last().Content);
        Assert.Contains("conversation: none yet", host.Model.Requests.Last().Content);
        Assert.Contains("typed so far: Keep", host.Model.Requests.Last().Content);
    }

    [Fact]
    public async Task A_long_conversation_is_trimmed_to_the_operations_budget()
    {
        using var host = new AgentsHost();
        Scenarios.GivenConferences(host.Storage);
        var conversation = host.Storage.Conversations.Start("long");
        for (var i = 0; i < 60; i++)
        {
            host.Storage.Conversations.Append(conversation.Id, Storage.TurnSpeaker.Person, $"Message number {i} about my conferences, with quite a lot of words in it so that the window fills up and has to be trimmed from the oldest end.");
            host.Storage.Conversations.Append(conversation.Id, Storage.TurnSpeaker.Assistant, $"Reply number {i}, also long enough to matter when sixty of them are laid end to end in one context.");
        }

        host.Model.Answer("suggestions", Five);
        var set = await host.Orchestrator.SuggestAsync(conversation.Id, null);

        Assert.True(set.FromModel);
        Assert.True(host.Model.Requests.Last().PromptTokens <= Operations.Suggestions.ContextBudget, $"{host.Model.Requests.Last().PromptTokens} prompt tokens");
        Assert.Contains("earlier turns not shown", host.Model.Requests.Last().Content);
    }

    [Fact]
    public void The_starters_and_the_cleaning_keep_the_first_tiers_words()
    {
        var kept = Suggestions.Clean(
            ["  \"Show my conferences\"  ", "Run a query", "show my CONFERENCES", "", "Add a column to conferences", "Show my <thing> from this year", "Keep this: …", "Which was the most expensive?"],
            ["How many conferences do I have?", "Keep these as well: …", "Take back the last change", "Show my conferences", "Drop something I no longer need from conferences", "What can you keep for me?", "One more", "And another"]);

        Assert.Equal(SuggestionSet.Most, kept.Count);
        Assert.Equal("Show my conferences", kept[0]);
        Assert.Equal("Which was the most expensive?", kept[1]);
        Assert.DoesNotContain(kept, static option => option.Contains("query", StringComparison.OrdinalIgnoreCase) || option.Contains("column", StringComparison.OrdinalIgnoreCase));
        Assert.Single(kept, static option => string.Equals(option, "Show my conferences", StringComparison.OrdinalIgnoreCase));

        // A placeholder left in is the model's, not an option; C#'s own "Keep these as well: …" is meant to be completed.
        Assert.DoesNotContain(kept, static option => option.Contains('<', StringComparison.Ordinal) || option == "Keep this: …");
        Assert.Contains("Keep these as well: …", kept);

        // Keeping again a title the model was given would be a duplicate with made-up values.
        var titled = Suggestions.Clean(
            ["Keep this: EuroPython, 2025, $150", "Remove EuroPython", "Show my conferences", "Keep this: the one in Odesa on 9 June 2025"],
            [], new NamedTitles("shown last", "conferences", ["EuroPython"], 1));
        Assert.Equal(["Remove EuroPython", "Show my conferences", "Keep this: the one in Odesa on 9 June 2025"], titled);
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
