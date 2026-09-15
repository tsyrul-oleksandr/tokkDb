using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TokkDb.Assistant.Agents.Operations;

namespace TokkDb.Assistant.Agents.Models;

/// <summary>
/// The chat client type (AG-1f). <b>Internal, and resolved from the container in exactly one
/// place</b>, the <see cref="OperationRunner"/>, so that calling a model outside an operation is
/// impossible rather than forbidden: nothing outside this assembly can name this type, and an
/// architecture test asserts that nothing inside it but the runner touches it.
///
/// What it does is small on purpose. It turns an operation's declaration into the options the
/// transport needs - the model, the window, the output cap, the answer's schema, the tools, the
/// loop bound - makes the call, and reports what it cost. The judgement about whether the answer
/// is usable, and the repair loop, are the runner's.
/// </summary>
internal sealed class ChatModel
{
    private readonly IChatClient _client;
    private readonly ModelSettings _settings;

    public ChatModel(IChatClient client, ModelSettings settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public ModelSettings Settings => _settings;

    /// <summary>The configuration the operation runs with (AG-7): its override, or the default.</summary>
    public ModelConfiguration ConfigurationFor(OperationDeclaration operation) =>
        _settings.Overrides.ContainsKey(operation.Name) ? _settings.For(operation.Name) : operation.Model ?? _settings.Default;

    public async Task<ModelReply> CompleteAsync(
        OperationDeclaration operation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(messages);

        var configuration = ConfigurationFor(operation);
        var counting = new CountingChatClient(_client);

        // The loop is bounded explicitly, and only where there is a loop to bound (R-1, D-5).
        IChatClient client = tools.Count == 0
            ? counting
            : counting.AsBuilder()
                .UseFunctionInvocation(configure: invoking => invoking.MaximumIterationsPerRequest = operation.MaxToolIterations)
                .Build();

        var options = new ChatOptions
        {
            ModelId = configuration.Model,
            Temperature = configuration.Temperature,
            MaxOutputTokens = operation.OutputBudget,
            Tools = tools.Count == 0 ? null : [.. tools],
            ResponseFormat = operation.OutputSchema is { } schema
                ? ChatResponseFormat.ForJsonSchema(JsonSerializer.Deserialize<JsonElement>(schema), operation.Name, operation.StepName)
                : null,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // The native transport's names (step 0.3): the window and the reasoning switch are
                // per-call options here, and a fake reads the operation's name to answer from its script.
                ["num_ctx"] = configuration.ContextSize,
                ["think"] = configuration.Think,
                ["operation"] = operation.Name
            }
        };

        var started = Stopwatch.GetTimestamp();
        var response = await client.GetResponseAsync(messages, options, cancellation).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);

        return new ModelReply(
            response.Text ?? string.Empty,
            counting.PromptTokens,
            counting.CompletionTokens,
            counting.RoundTrips,
            counting.PeakContext,
            elapsed,
            response.FinishReason?.Value);
    }

    /// <summary>
    /// Round trips counted apart from calls (§6.2a), at the one place they can be: under the
    /// function-invoking client, where each turn of its loop is one call through here.
    /// </summary>
    private sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int RoundTrips { get; private set; }
        public int PromptTokens { get; private set; }
        public int CompletionTokens { get; private set; }
        public int PeakContext { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RoundTrips++;
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            var prompt = (int)(response.Usage?.InputTokenCount ?? 0);
            var completion = (int)(response.Usage?.OutputTokenCount ?? 0);

            PromptTokens += prompt;
            CompletionTokens += completion;
            PeakContext = Math.Max(PeakContext, prompt + completion);

            return response;
        }
    }
}
