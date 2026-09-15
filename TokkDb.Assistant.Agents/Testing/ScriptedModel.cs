using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Agents.Testing;

/// <summary>One thing the fake was asked: which operation, and what it was sent.</summary>
public sealed record ScriptedRequest(string Operation, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>The system message, which is the operation's prefix (AG-6a).</summary>
    public string Prefix => string.Join("\n", Messages.Where(static m => m.Role == ChatRole.System).Select(static m => m.Text));

    /// <summary>Everything the person's side said, which is the per-call content.</summary>
    public string Content => string.Join("\n", Messages.Where(static m => m.Role == ChatRole.User).Select(static m => m.Text));

    /// <summary>The whole prompt as text, for asserting what reached the model and what did not.</summary>
    public string Prompt => string.Join("\n", Messages.Select(static m => m.Text));

    public int PromptTokens => Tokens.Estimate(Prompt);
}

/// <summary>
/// A model that is not a model (D-12, step 4.2): a deterministic <see cref="IChatClient"/> that
/// answers from a script, can be made to return malformed output, and records what it was asked.
///
/// The script is keyed by operation name, which the runner puts in the call's options: an
/// operation can be driven end to end with no Ollama running, and a test can say exactly what
/// each call answers. Answers are queued per operation and consumed in order; an operation with
/// nothing scripted gets an empty reply, which is the failure the spike actually saw.
///
/// Token counts are estimated with the same estimator the budgets use, so a scenario's cost is
/// measurable against the fake and the harness can assert a budget before a model is ever run.
/// </summary>
public sealed class ScriptedModel : IChatClient
{
    private readonly Dictionary<string, Queue<Func<ScriptedRequest, ChatMessage>>> _script = new(StringComparer.Ordinal);
    private readonly List<ScriptedRequest> _requests = [];
    private readonly object _gate = new();

    /// <summary>Every call, in order, with what it was sent.</summary>
    public IReadOnlyList<ScriptedRequest> Requests => _requests;

    /// <summary>How many calls an operation received.</summary>
    public int Calls(string operation) => _requests.Count(request => request.Operation == operation);

    public int TotalCalls => _requests.Count;

    /// <summary>How long each answer takes, for tests about timeouts. Zero by default.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>The next answer this operation gives, as text.</summary>
    public ScriptedModel Answer(string operation, string text) => Answer(operation, _ => text);

    /// <summary>The next answer this operation gives, computed from what it was asked.</summary>
    public ScriptedModel Answer(string operation, Func<ScriptedRequest, string> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Reply(operation, request => new ChatMessage(ChatRole.Assistant, answer(request)));
    }

    /// <summary>
    /// The next answer this operation gives is a call of a tool, which the function-invoking
    /// client runs before asking again; the answer after that is the one it gets back.
    /// </summary>
    public ScriptedModel CallTool(string operation, string tool, IDictionary<string, object?>? arguments = null) =>
        Reply(operation, _ => new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call-{Guid.NewGuid():N}", tool, arguments)]));

    private ScriptedModel Reply(string operation, Func<ScriptedRequest, ChatMessage> answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            if (!_script.TryGetValue(operation, out var queue))
            {
                queue = new Queue<Func<ScriptedRequest, ChatMessage>>();
                _script[operation] = queue;
            }

            queue.Enqueue(answer);
        }

        return this;
    }

    /// <summary>The same answer, every time this operation is asked, until something else is scripted.</summary>
    public ScriptedModel Always(string operation, string text)
    {
        for (var i = 0; i < 64; i++) Answer(operation, text);
        return this;
    }

    /// <summary>Malformed output - not JSON at all - for the next <paramref name="times"/> calls (AG-5).</summary>
    public ScriptedModel Malformed(string operation, int times = 1)
    {
        for (var i = 0; i < times; i++) Answer(operation, "I think the answer is: {\"not\": valid json");
        return this;
    }

    /// <summary>An empty reply for the next call: what a filled window produces.</summary>
    public ScriptedModel Empty(string operation) => Answer(operation, string.Empty);

    /// <summary>The next call does not come back in time: what the transport raises when the model stalls.</summary>
    public ScriptedModel Timeout(string operation) =>
        Reply(operation, static _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

    /// <summary>The next answer is cut off at the output cap (R-2b): the text is what came back before the cap, and the finish reason says so.</summary>
    public ScriptedModel CutOff(string operation, string partialText)
    {
        lock (_gate) _cutOff.Add((operation, partialText));
        return Answer(operation, partialText);
    }

    private readonly HashSet<(string Operation, string Text)> _cutOff = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var operation = options?.AdditionalProperties?.TryGetValue("operation", out var named) == true
            ? named?.ToString() ?? "?"
            : "?";

        var request = new ScriptedRequest(operation, list, options);
        ChatMessage message;

        lock (_gate)
        {
            _requests.Add(request);
            message = _script.TryGetValue(operation, out var queue) && queue.Count > 0
                ? queue.Dequeue()(request)
                : new ChatMessage(ChatRole.Assistant, string.Empty);
        }

        var text = message.Text;
        var prompt = request.PromptTokens;
        var completion = Math.Max(Tokens.Estimate(text), message.Contents.OfType<FunctionCallContent>().Any() ? 20 : 0);

        var response = new ChatResponse(message)
        {
            ModelId = "scripted",
            FinishReason = message.Contents.OfType<FunctionCallContent>().Any()
                ? ChatFinishReason.ToolCalls
                : text.Length == 0 || _cutOff.Contains((operation, text)) ? ChatFinishReason.Length : ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = prompt,
                OutputTokenCount = completion,
                TotalTokenCount = prompt + completion
            }
        };

        return Latency > TimeSpan.Zero
            ? Task.Delay(Latency, cancellationToken).ContinueWith(_ => response, cancellationToken)
            : Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
