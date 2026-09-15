using System.Text.Json;
using Microsoft.Extensions.AI;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// A model that is not a model (D-12, step 4.2), driven through the real runner: an operation
/// runs end to end with no Ollama running, malformed output is repaired within the bound (AG-5)
/// and visible in the trace, and the fake records what it was asked.
/// </summary>
public sealed class ScriptedModelTests
{
    static ScriptedModelTests() => Operations.Include(typeof(TestOperations).Assembly);

    private static AssembledContext Context(string content) =>
        new(Operations.Intent.Instructions, content, EgressClass.Nothing, "the message");

    private static Parsed<string> Intent(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String
                ? Parsed<string>.Ok(intent.GetString()!)
                : Parsed<string>.Invalid("no intent in the answer");
        }
        catch (JsonException failure)
        {
            return Parsed<string>.Invalid("not JSON: " + failure.Message);
        }
    }

    [Fact]
    public async Task An_operation_runs_end_to_end_with_no_model_running()
    {
        using var host = new AgentsHost();
        host.Model.Answer(Operations.Intent.Name, """{"intent":"find"}""");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var result = await host.Runner.RunAsync(Operations.Intent, Context("how many conferences last year"), Intent, request.Id);

        Assert.Equal("find", result.Value);
        Assert.Equal(ModelOutcome.Valid, result.Outcome);

        // The step, in the trace, with its model call (TR-3): counts, a hash, no prompt.
        var (_, steps) = host.Recorder.Read(request.Id)!.Value;
        var step = Assert.Single(steps);
        Assert.Equal("what was meant", step.Name);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Equal("the message", step.Input);
        Assert.NotNull(step.Call);
        Assert.True(step.Call.PromptTokens > 0);
        Assert.Equal(1, step.Call.RoundTrips);
        Assert.Equal(0, step.Call.Retries);
        Assert.Equal(Hashes.Length, step.Call.PromptHash.Length);
        Assert.DoesNotContain("conferences", step.Call.PromptHash);

        // And the fake recorded what it was asked, keyed by the operation.
        var asked = Assert.Single(host.Model.Requests);
        Assert.Equal(Operations.Intent.Name, asked.Operation);
        Assert.Contains("how many conferences last year", asked.Content);
        Assert.Equal(Operations.Intent.Instructions.Trim(), asked.Prefix.Trim());
    }

    /// <summary>AG-5: invalid twice, valid on the third attempt, completes - and the trace shows the retries.</summary>
    [Fact]
    public async Task Malformed_output_is_repaired_within_the_bound()
    {
        using var host = new AgentsHost();
        host.Model.Malformed(Operations.Intent.Name, 2).Answer(Operations.Intent.Name, """{"intent":"store"}""");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var result = await host.Runner.RunAsync(Operations.Intent, Context("keep this"), Intent, request.Id);

        Assert.Equal("store", result.Value);
        Assert.Equal(ModelOutcome.MalformedRepaired, result.Outcome);
        Assert.Equal(2, result.Call.Retries);
        Assert.Equal(3, result.Call.RoundTrips);
        Assert.Equal(3, host.Model.TotalCalls);

        // The repair sends the error, not the whole request again (§6.1): the third call carries
        // the two failed answers and what was wrong with them.
        var third = host.Model.Requests[2];
        Assert.Contains("could not be used", third.Content);
        Assert.Equal(2, third.Messages.Count(static message => message.Role == ChatRole.Assistant));
    }

    /// <summary>AG-5: one that never succeeds fails with a stated reason, and the failure is in the trace.</summary>
    [Fact]
    public async Task Malformed_output_past_the_bound_fails_with_a_stated_reason()
    {
        using var host = new AgentsHost();
        host.Model.Malformed(Operations.Intent.Name, 5);
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var failure = await Assert.ThrowsAsync<ModelFailedException>(() =>
            host.Runner.RunAsync(Operations.Intent, Context("keep this"), Intent, request.Id));

        Assert.Equal(ModelOutcome.MalformedPastBound, failure.Outcome);
        Assert.Contains("Nothing was changed", failure.Reason);
        Assert.Equal(Operations.Intent.MaxRepairs + 1, host.Model.TotalCalls);

        var step = Assert.Single(host.Recorder.Read(request.Id)!.Value.Steps);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Equal(Operations.Intent.MaxRepairs, step.Call!.Retries);
        Assert.Contains("malformed", step.Output);
    }

    /// <summary>An empty reply is its own failure mode (AG-1e), not a malformed one.</summary>
    [Fact]
    public async Task An_empty_reply_fails_as_an_empty_reply()
    {
        using var host = new AgentsHost();
        host.Model.Empty(Operations.Intent.Name);
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var failure = await Assert.ThrowsAsync<ModelFailedException>(() =>
            host.Runner.RunAsync(Operations.Intent, Context("keep this"), Intent, request.Id));

        Assert.Equal(ModelOutcome.EmptyReply, failure.Outcome);
        Assert.Equal(1, host.Model.TotalCalls);
        Assert.Equal(StepStatus.Failed, Assert.Single(host.Recorder.Read(request.Id)!.Value.Steps).Status);
    }

    /// <summary>A timeout is a timeout: the cancellation the transport raises becomes a stated failure.</summary>
    [Fact]
    public async Task A_model_that_does_not_answer_in_time_fails_as_a_timeout()
    {
        using var host = new AgentsHost();
        host.Model.Latency = TimeSpan.FromSeconds(30);
        host.Model.Answer(Operations.Intent.Name, """{"intent":"store"}""");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // The caller's own cancellation is theirs; the transport's internal timeout is the
        // runner's to classify. Both leave the step failed rather than running (TR-4b).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Runner.RunAsync(Operations.Intent, Context("keep this"), Intent, request.Id, cancellation: cancel.Token));
    }

    /// <summary>The bounded tool loop, gone round once by the fake: one tool call, two round trips.</summary>
    [Fact]
    public async Task A_tool_call_goes_round_the_bounded_loop()
    {
        var shouted = new List<string>();
        var catalogue = new ToolCatalog().Add(AIFunctionFactory.Create((string text) =>
        {
            shouted.Add(text);
            return text.ToUpperInvariant();
        }, "shout", "Shouts."));

        using var host = new AgentsHost(tools: catalogue);
        host.Model
            .CallTool("shouting", "shout", new Dictionary<string, object?> { ["text"] = "hello" })
            .Answer("shouting", """{"said":"HELLO"}""");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var result = await host.Runner.RunAsync(TestOperations.Shouting,
            new AssembledContext(TestOperations.Shouting.Instructions, "hello", EgressClass.Nothing),
            static text => Parsed<string>.Ok(text), request.Id);

        Assert.Contains("HELLO", result.Value);
        Assert.Equal(["hello"], shouted);
        Assert.Equal(2, result.Call.RoundTrips);
        Assert.Equal(2, host.Model.TotalCalls);

        // The second call carried the tool's result back.
        Assert.Contains(host.Model.Requests[1].Messages, static message => message.Contents.OfType<FunctionResultContent>().Any());
    }

    /// <summary>R-1's condition: the loop is bounded explicitly, so a model that keeps calling stops.</summary>
    [Fact]
    public async Task A_model_that_keeps_calling_the_tool_is_stopped_by_the_bound()
    {
        var calls = 0;
        var catalogue = new ToolCatalog().Add(AIFunctionFactory.Create((string text) =>
        {
            calls++;
            return text;
        }, "shout", "Shouts."));

        using var host = new AgentsHost(tools: catalogue);
        for (var i = 0; i < 10; i++)
        {
            host.Model.CallTool("shouting", "shout", new Dictionary<string, object?> { ["text"] = "again" });
        }

        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        try
        {
            await host.Runner.RunAsync(TestOperations.Shouting,
                new AssembledContext(TestOperations.Shouting.Instructions, "hello", EgressClass.Nothing),
                static text => text.Length == 0 ? Parsed<string>.Invalid("empty") : Parsed<string>.Ok(text), request.Id);
        }
        catch (ModelFailedException)
        {
            // Failing is an acceptable end; looping is not.
        }

        Assert.True(calls <= TestOperations.Shouting.MaxToolIterations * (TestOperations.Shouting.MaxRepairs + 1),
            $"the tool ran {calls} times");
        Assert.True(host.Model.TotalCalls <= (TestOperations.Shouting.MaxToolIterations + 1) * (TestOperations.Shouting.MaxRepairs + 1),
            $"the model was called {host.Model.TotalCalls} times");
    }
}
