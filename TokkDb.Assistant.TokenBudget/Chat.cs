using TokkDb.Assistant.Agents.Orchestration;

namespace TokkDb.Assistant.TokenBudget;

/// <summary>The minimal chat display: a line in, the reply out, a question answered with y or n.</summary>
public static class Chat
{
    public static async Task<int> RunAsync(string databasePath, TextReader input, TextWriter output)
    {
        using var app = Composition.OverOllama(databasePath);
        foreach (var (recovered, _) in await app.Orchestrator.RecoverAsync())
        {
            output.WriteLine($"[recovered {recovered.Before.Operation}: {recovered.Action}]");
        }

        Ulid? conversation = null;
        Ulid? open = null;

        output.WriteLine("Type something to keep it, ask for it, or change it. An empty line ends.");
        while (true)
        {
            output.Write("> ");
            var line = await input.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) return 0;

            TurnOutcome outcome;
            if (open is { } request && line.Trim().ToLowerInvariant() is "y" or "n" or "yes" or "no")
            {
                outcome = await app.Orchestrator.AnswerAsync(request, line.Trim().StartsWith('y'));
                open = null;
            }
            else
            {
                var attachments = line.Split(' ').Where(static word => File.Exists(word)).ToList();
                var text = string.Join(' ', line.Split(' ').Where(word => !attachments.Contains(word)));
                outcome = await app.Orchestrator.HandleAsync(new TurnInput(conversation, text, attachments));
                conversation = outcome.ConversationId;
            }

            output.WriteLine(outcome.Reply);
            if (outcome.Results is { } page)
            {
                for (var i = 0; i < page.Records.Count; i++)
                {
                    output.WriteLine($"  {i + 1}. {page.Titles[i]}: {string.Join(", ", page.Records[i].Fields.Select(field => $"{field.Key}={field.Value}"))}");
                }
            }

            if (outcome.IsWaiting)
            {
                open = outcome.RequestId;
                output.WriteLine($"  [{outcome.Question!.Yes} = y, {outcome.Question.No} = n]");
            }
        }
    }
}
