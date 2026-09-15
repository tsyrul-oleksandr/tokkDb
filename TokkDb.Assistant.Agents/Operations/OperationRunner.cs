using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Operations;

/// <summary>
/// The context of one call, assembled and measured before the model sees it (AG-1a, AG-6a).
/// </summary>
/// <param name="Prefix">
/// Instructions, schema block, tools block, in that order: byte-identical between calls of one
/// operation while the storage is unchanged, which is what the model's prefix cache is keyed on.
/// </param>
/// <param name="Content">The per-call part: the message, the sample, the question.</param>
/// <param name="Egress">What of the user's the content carries (NF-4a).</param>
/// <param name="Summary">One line about the content for the step's input, never the content itself.</param>
public sealed record AssembledContext(string Prefix, string Content, EgressClass Egress, string? Summary = null)
{
    /// <summary>What the call will cost, estimated the way the budgets are (over rather than under).</summary>
    public int EstimatedTokens => Tokens.Estimate(Prefix) + Tokens.Estimate(Content);

    /// <summary>The hash of the prefix, which AG-6a asserts is the same from one call to the next.</summary>
    public string PrefixHash => Hashes.Of(Prefix);
}

/// <summary>Whether an answer was usable, and if not, why - which is what goes back to the model (AG-5).</summary>
public readonly record struct Parsed<T>(T? Value, string? Problem)
{
    public bool IsValid => Problem is null;

    public static Parsed<T> Ok(T value) => new(value, null);

    public static Parsed<T> Invalid(string problem) => new(default, problem);
}

/// <summary>Reads the model's text into a value, or says what was wrong with it.</summary>
public delegate Parsed<T> OutputParser<T>(string text);

/// <summary>What an operation produced, and what it cost.</summary>
public sealed record OperationResult<T>(T Value, ExecutionStep Step, ModelOutcome Outcome, string RawText)
{
    public ModelCall Call => Step.Call!;
}

/// <summary>
/// The assembled context would exceed the operation's budget (AG-1a). The assembler trims what it
/// can deterministically before this; what is left is refused, never sent, and never quietly cut.
/// </summary>
public sealed class ContextBudgetExceededException : Exception
{
    public ContextBudgetExceededException(OperationDeclaration operation, int estimated)
        : base($"Operation '{operation.Name}' was given a context of about {estimated} tokens against a budget of {operation.ContextBudget}.")
    {
        Operation = operation;
        Estimated = estimated;
    }

    public OperationDeclaration Operation { get; }
    public int Estimated { get; }
}

/// <summary>A context would send more of the user's data than the operation declared (NF-4a).</summary>
public sealed class EgressExceededException : Exception
{
    public EgressExceededException(OperationDeclaration operation, EgressClass offered)
        : base($"Operation '{operation.Name}' declares it sends {operation.Egress}, and was given a context that carries {offered}.")
    {
        Operation = operation;
        Offered = offered;
    }

    public OperationDeclaration Operation { get; }
    public EgressClass Offered { get; }
}

/// <summary>
/// The one public way to call a model (AG-1, AG-1f, AG-5, TR-3).
///
/// It takes an operation and a context, checks the context against the operation's budget and
/// egress before anything is sent, resolves the chat client - here and nowhere else - makes the
/// call, validates the answer and sends a malformed one back for repair within the operation's
/// bound, and records the step with its model call: model, token counts both ways, round trips,
/// retries, peak context, duration and a hash of the prompt, never the prompt (D-8).
/// </summary>
public sealed class OperationRunner
{
    private readonly IServiceProvider _services;
    private readonly ITraceRecorder _recorder;
    private readonly ToolCatalog _tools;

    public OperationRunner(IServiceProvider services, ITraceRecorder recorder, ToolCatalog tools)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    /// <summary>The model configuration an operation would run with (AG-7), for the interface and the harness.</summary>
    public ModelConfiguration ConfigurationFor(OperationDeclaration operation) =>
        _services.GetRequiredService<ChatModel>().ConfigurationFor(operation);

    /// <summary>
    /// Runs the operation and returns its parsed answer, recording the step under
    /// <paramref name="requestId"/> whatever happens.
    /// </summary>
    /// <exception cref="ContextBudgetExceededException">The context is over the operation's budget (AG-1a).</exception>
    /// <exception cref="EgressExceededException">The context carries more than the operation declared (NF-4a).</exception>
    /// <exception cref="ModelFailedException">No usable answer within the repair bound, an empty reply, or a timeout (AG-5).</exception>
    public async Task<OperationResult<T>> RunAsync<T>(
        OperationDeclaration operation,
        AssembledContext context,
        OutputParser<T> parse,
        Ulid requestId,
        Ulid? afterStep = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parse);

        // Enforced before the call, not documented (AG-1a): the model's window is a capability
        // limit and never the target, and an operation over budget fails here rather than
        // degrading quietly.
        var estimated = context.EstimatedTokens;
        if (estimated > operation.ContextBudget)
        {
            throw new ContextBudgetExceededException(operation, estimated);
        }

        if (context.Egress > operation.Egress)
        {
            throw new EgressExceededException(operation, context.Egress);
        }

        var tools = _tools.For(operation);

        // AG-1f: the only place the chat client is resolved.
        var model = _services.GetRequiredService<ChatModel>();
        var configuration = model.ConfigurationFor(operation);

        var step = new ExecutionStep(Ulid.NewUlid(), requestId, operation.StepName, StepStatus.Running, DateTimeOffset.UtcNow)
        {
            After = afterStep,
            Input = context.Summary
        };
        _recorder.Record(step);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, context.Prefix),
            new(ChatRole.User, context.Content)
        };

        var promptHash = Hashes.Of(context.Prefix + "\n" + context.Content);
        var retries = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        var roundTrips = 0;
        var peak = 0;
        var elapsed = TimeSpan.Zero;
        string? lastText = null;
        string? lastProblem = null;

        for (var attempt = 0; attempt <= operation.MaxRepairs; attempt++)
        {
            ModelReply reply;

            try
            {
                reply = await model.CompleteAsync(operation, messages, tools, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The caller's own cancellation (N-7): the step says so and the cancellation is theirs to handle.
                Finish(step, StepStatus.Failed, "cancelled", Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));
                throw;
            }
            catch (Exception failure) when (failure is OperationCanceledException or HttpRequestException or TimeoutException)
            {
                // The transport's own timeout, or no model to reach: a stated failure, by mode (AG-1e).
                Finish(step, StepStatus.Failed, failure.Message, Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));
                throw new ModelFailedException(operation.Name, ModelOutcome.Timeout, "The model could not be reached, or did not answer in time.", failure);
            }

            promptTokens += reply.PromptTokens;
            completionTokens += reply.CompletionTokens;
            roundTrips += reply.RoundTrips;
            peak = Math.Max(peak, reply.PeakContextTokens);
            elapsed += reply.Elapsed;
            lastText = reply.Text;

            if (reply.IsEmpty)
            {
                Finish(step, StepStatus.Failed, "the model produced nothing",
                    Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));
                throw new ModelFailedException(operation.Name, ModelOutcome.EmptyReply,
                    "The model produced no answer. This happens when its window fills; the request was not applied.");
            }

            var parsed = parse(reply.Text);

            if (parsed.IsValid)
            {
                var outcome = retries == 0 ? ModelOutcome.Valid : ModelOutcome.MalformedRepaired;
                var done = Finish(step, StepStatus.Completed, Bounded(reply.Text),
                    Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));

                return new OperationResult<T>(parsed.Value!, done, outcome, reply.Text);
            }

            lastProblem = parsed.Problem;

            // A cut-off answer is not malformed, it is unfinished, and asking again would cut it at
            // the same place: the operation fails at once, saying what the cap was (R-2b, AG-1e).
            if (reply.WasCutOff)
            {
                Finish(step, StepStatus.Failed, $"cut off at the output cap of {operation.OutputBudget} tokens: {parsed.Problem}",
                    Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));
                throw new ModelFailedException(operation.Name, ModelOutcome.CutOff,
                    $"The answer was longer than the {operation.OutputBudget} tokens allowed for it and was cut off, so it could not be used. Nothing was changed. Try with less at a time.");
            }

            if (attempt == operation.MaxRepairs) break;

            // The repair loop sends the error, not the whole request again (§6.1): the answer
            // and what was wrong with it are appended, and the model is asked once more.
            retries++;
            messages.Add(new ChatMessage(ChatRole.Assistant, reply.Text));
            messages.Add(new ChatMessage(ChatRole.User, $"That answer could not be used: {parsed.Problem}. Answer again, with only the answer."));
        }

        Finish(step, StepStatus.Failed, $"malformed past {operation.MaxRepairs} repairs: {lastProblem}",
            Call(configuration, promptHash, promptTokens, completionTokens, roundTrips, retries, peak, elapsed));

        throw new ModelFailedException(operation.Name, ModelOutcome.MalformedPastBound,
            $"The model's answer could not be used after {retries + 1} attempts: {lastProblem}. Nothing was changed." +
            (lastText is null ? "" : $" Last answer: {Bounded(lastText, 200)}"));
    }

    private ExecutionStep Finish(ExecutionStep step, StepStatus status, string? output, ModelCall call)
    {
        var done = step with { Status = status, EndedAt = DateTimeOffset.UtcNow, Output = output, Call = call };
        _recorder.Record(done);
        return done;
    }

    private static ModelCall Call(
        ModelConfiguration configuration, string promptHash, int promptTokens, int completionTokens,
        int roundTrips, int retries, int peak, TimeSpan elapsed) =>
        new(configuration.Model, promptTokens, completionTokens, elapsed, promptHash)
        {
            RoundTrips = roundTrips,
            Retries = retries,
            PeakContextTokens = peak
        };

    /// <summary>A step's output is what the model answered, bounded: the panel shows it, the file keeps it.</summary>
    private static string Bounded(string text, int limit = 4_000) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
