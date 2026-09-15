using System.Text.RegularExpressions;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// UI-2 and 9.3: the vocabulary review as a test. Every string the first tier shows - the
/// conversation's replies and cards, the browser's surfaces, and the names a screen reader says
/// there - is read from the sources that hold them and checked for the words that require
/// knowing what a database is. The second tier (the detail panel, the diagnostics line) may say
/// "index seek", and the third (what a thing keeps) glosses. docs/assistant-vocabulary-review.md
/// lists the tiers and which sources belong to each.
/// </summary>
public sealed partial class VocabularyTests
{
    /// <summary>UI-2's words, and BR-6's two, as whole words in any case.</summary>
    private static readonly string[] Forbidden = ["collection", "column", "index", "schema", "query", "queries", "constraint", "nullable", "database", "sql", "primary key", "foreign key"];

    /// <summary>The sources whose string literals reach the first tier.</summary>
    private static readonly string[] FirstTier =
    [
        "TokkDb.Assistant.Agents/Orchestration/Replies.cs",
        "TokkDb.Assistant.Agents/Changes/ConfirmationCard.cs",
        "TokkDb.Assistant.Agents/Changes/StructuralAction.cs",
        "TokkDb.Assistant.Agents/Changes/ChangeClassifier.cs",
        "TokkDb.Assistant.Agents/Browsing/WhatItKeeps.cs",
        "TokkDb.Assistant.Agents/Browsing/ThingsOverview.cs",
        "TokkDb.Assistant.Agents/Browsing/RecordDetail.cs",
        "TokkDb.Assistant.Agents/Browsing/TableFilter.cs",
        "TokkDb.Assistant.Application/Browse/BrowseSurface.cs",
        "TokkDb.Assistant.Application/Browse/BrowseViewModel.cs",
        "TokkDb.Assistant.Application/Chat/ConversationView.cs",
        "TokkDb.Assistant.Application/Chat/ChatViewModel.cs",
        "TokkDb.Assistant.Application/Shell/MainPage.cs",
        "TokkDb.Assistant.Application/Shell/ConversationList.cs"
    ];

    [GeneratedRegex("\\$?@?\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Literal();

    [GeneratedRegex("\\{(?:[^{}]|\\{[^{}]*\\})*\\}")]
    private static partial Regex Interpolation();

    [Fact]
    public void The_first_tier_contains_no_database_vocabulary()
    {
        var root = RepositoryRoot();
        var offences = new List<string>();

        foreach (var file in FirstTier)
        {
            var path = Path.Combine(root, file);
            Assert.True(File.Exists(path), $"{file} is listed in the review but is not there");

            foreach (var (line, number) in File.ReadLines(path).Select(static (line, index) => (line, index + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;

                // The list of forbidden words is not something a person is shown.
                if (line.Contains("ForbiddenWords", StringComparison.Ordinal)) continue;

                // Interpolation holes are code, not words: they go before the literals are read.
                foreach (Match match in Literal().Matches(Interpolation().Replace(line, " ")))
                {
                    var text = match.Groups[1].Value;
                    foreach (var word in Forbidden)
                    {
                        if (Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))
                        {
                            offences.Add($"{file}:{number}: \"{match.Groups[1].Value}\" says \"{word}\"");
                        }
                    }
                }
            }
        }

        Assert.True(offences.Count == 0, "First-tier strings with database vocabulary:\n" + string.Join("\n", offences));
    }

    /// <summary>The second tier is allowed to be technical, and is: the diagnostics line names the access path.</summary>
    [Fact]
    public void The_detail_panel_still_says_what_it_means()
    {
        var root = RepositoryRoot();
        var detail = File.ReadAllText(Path.Combine(root, "TokkDb.Assistant.Application/Diagram/DetailViews.cs"));
        var execution = File.ReadAllText(Path.Combine(root, "TokkDb.Assistant.Storage.Engine/EngineQuery.cs"));

        Assert.Contains("tokens in", detail);
        Assert.Contains("prompt hash", detail);
        Assert.Contains("index", execution, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The review itself is listed, and names the tiers and the sources this test reads.</summary>
    [Fact]
    public void The_review_is_listed()
    {
        var review = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/assistant-vocabulary-review.md"));

        Assert.Contains("First tier", review);
        Assert.Contains("Second tier", review);
        Assert.Contains("Third tier", review);
        foreach (var file in FirstTier) Assert.Contains(Path.GetFileName(file), review);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TokkDb.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }
}
