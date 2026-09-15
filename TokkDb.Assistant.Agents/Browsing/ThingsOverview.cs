using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Browsing;

/// <summary>One thing stored, as the overview shows it (BR-1): the name it is known by, what it keeps, how many, and when.</summary>
public sealed record ThingCard(string Thing, string Title, string Keeps, long HowMany, DateTimeOffset? LastChanged)
{
    public string Count => HowMany switch { 0 => "none yet", 1 => "1 of them", _ => $"{HowMany} of them" };

    public string When => ThingsOverview.Ago(LastChanged);
}

/// <summary>
/// The browser's first screen (BR-1, BR-1a): everything stored, most recently changed first,
/// from the maintained counts and times - never a read of the records.
/// </summary>
public static class ThingsOverview
{
    public static IReadOnlyList<ThingCard> Of(IStorage storage, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(storage);

        return
        [
            .. storage.Overview().Select(thing => new ThingCard(
                thing.Name,
                Title(thing.Definition),
                thing.Definition.Purpose ?? $"{Title(thing.Definition)} you keep",
                thing.RecordCount,
                thing.LastChanged))
        ];
    }

    /// <summary>The name as a person sees it: "Conference expenses", not "conference_expenses".</summary>
    public static string Title(CollectionDefinition definition)
    {
        var words = WhatItKeeps.Plain(definition.Name);
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>When, in words: "just now", "2 hours ago", "Tuesday", "5 Sept".</summary>
    public static string Ago(DateTimeOffset? moment, DateTimeOffset? now = null)
    {
        if (moment is null) return "never";

        var age = (now ?? DateTimeOffset.UtcNow) - moment.Value;
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 7 ? moment.Value.LocalDateTime.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture)
            : moment.Value.LocalDateTime.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
    }
}
