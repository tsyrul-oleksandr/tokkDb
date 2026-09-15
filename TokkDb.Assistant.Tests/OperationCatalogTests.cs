using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TokkDb.Assistant.Agents;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Testing;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb.Assistant.Tests.Architecture;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// A new operation, added in one file and nowhere else (EX-2, step 4.1). This is the whole of
/// what adding one takes: a declaration. <see cref="Operations.All"/> finds it once its assembly
/// is included, the runner runs it, and the orchestrator was not touched.
/// </summary>
public static class TestOperations
{
    public static readonly OperationDeclaration Echo = new(
        Name: "echo",
        StepName: "saying it back",
        Instructions: "Say back what you were given.",
        Tools: [],
        ContextBudget: 500,
        OutputBudget: 100,
        Egress: EgressClass.Nothing,
        OutputSchema: """{"type":"object","properties":{"said":{"type":"string"}},"required":["said"],"additionalProperties":false}""");

    /// <summary>An operation that carries a tool, so the bounded loop is exercised by the fake.</summary>
    public static readonly OperationDeclaration Shouting = new(
        Name: "shouting",
        StepName: "shouting it",
        Instructions: "Shout what you were given, using the tool, then answer.",
        Tools: ["shout"],
        ContextBudget: 500,
        OutputBudget: 100,
        Egress: EgressClass.Nothing,
        OutputSchema: """{"type":"object","properties":{"said":{"type":"string"}},"required":["said"],"additionalProperties":false}""",
        MaxToolIterations: 2);
}

/// <summary>
/// Operations (AG-1, AG-1a, AG-1f, AG-7, D-5, EX-2, NF-4a, step 4.1): each declares all three,
/// the assembler refuses rather than trims, a new operation is one file, and the chat client type
/// is reachable from exactly one place.
/// </summary>
public sealed class OperationCatalogTests
{
    static OperationCatalogTests() => Operations.Include(typeof(TestOperations).Assembly);

    private static readonly Assembly[] AssistantAssemblies =
    [
        typeof(IStorage).Assembly,
        typeof(TokkDbStorage).Assembly,
        typeof(Ingestion.Table).Assembly,
        typeof(RequestTrace).Assembly,
        typeof(OperationCatalogTests).Assembly
    ];

    [Fact]
    public void Every_operation_declares_a_model_configuration_a_tool_subset_and_a_context_budget()
    {
        using var host = new AgentsHost();
        var operations = Operations.All;

        Assert.NotEmpty(operations);
        Assert.Equal(operations.Count, operations.Select(static operation => operation.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var operation in operations)
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.Name), "an operation has no name");
            Assert.False(string.IsNullOrWhiteSpace(operation.StepName), $"{operation.Name} has no step name");
            Assert.False(string.IsNullOrWhiteSpace(operation.Instructions), $"{operation.Name} has no instructions");
            Assert.NotNull(operation.Tools);
            Assert.True(operation.ContextBudget > 0, $"{operation.Name} has no context budget");
            Assert.True(operation.OutputBudget > 0, $"{operation.Name} has no output budget");
            Assert.True(Enum.IsDefined(operation.Egress), $"{operation.Name} declares no egress class");

            // AG-7: the configuration it runs with, its own or the default, and never null.
            var configuration = host.Runner.ConfigurationFor(operation);
            Assert.False(string.IsNullOrWhiteSpace(configuration.Model));
            Assert.True(configuration.ContextSize > operation.ContextBudget, $"{operation.Name}'s window is not above its budget");
        }

        // The seven of §6.2 are all here, with the budgets the table gives them.
        Assert.Equal(600, Operations.Intent.ContextBudget);
        Assert.Equal(4_000, Operations.Extraction.ContextBudget);
        Assert.Equal(3_000, Operations.Mapping.ContextBudget);
        Assert.Equal(2_000, Operations.Structure.ContextBudget);
        Assert.Equal(2_500, Operations.Query.ContextBudget);
        Assert.Equal(1_500, Operations.Digest.ContextBudget);
        Assert.Equal(1_200, Operations.Phrase.ContextBudget);
    }

    /// <summary>AG-7: extraction and mapping can be pointed at different models without a code change.</summary>
    [Fact]
    public void Operations_can_be_pointed_at_different_models_by_settings()
    {
        var settings = new ModelSettings(new ModelConfiguration("ollama", "qwen3.5:4b", 16_384, 0.2f))
            .Override(Operations.Extraction.Name, new ModelConfiguration("ollama", "gemma3:12b", 32_768, 0.1f));

        using var host = new AgentsHost(settings: settings);

        Assert.Equal("gemma3:12b", host.Runner.ConfigurationFor(Operations.Extraction).Model);
        Assert.Equal("qwen3.5:4b", host.Runner.ConfigurationFor(Operations.Mapping).Model);
    }

    /// <summary>AG-1a: over budget fails before the call, rather than degrading quietly.</summary>
    [Fact]
    public async Task The_assembler_refuses_a_context_over_the_budget_rather_than_trimming_it()
    {
        using var host = new AgentsHost();
        host.Model.Always(Operations.Intent.Name, """{"intent":"store"}""");

        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");
        var tooMuch = new AssembledContext("instructions", string.Join(" ", Enumerable.Repeat("word", 3_000)), EgressClass.Nothing);

        var refused = await Assert.ThrowsAsync<ContextBudgetExceededException>(() =>
            host.Runner.RunAsync(Operations.Intent, tooMuch, Parse, request.Id));

        Assert.Equal(Operations.Intent, refused.Operation);
        Assert.True(refused.Estimated > Operations.Intent.ContextBudget);
        Assert.Equal(0, host.Model.TotalCalls);
    }

    /// <summary>NF-4a: an operation cannot be handed more of the user's data than it declared.</summary>
    [Fact]
    public async Task An_operation_cannot_send_more_than_it_declared()
    {
        using var host = new AgentsHost();
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");
        var rawText = new AssembledContext("instructions", "the whole of a document", EgressClass.RawText);

        await Assert.ThrowsAsync<EgressExceededException>(() =>
            host.Runner.RunAsync(Operations.Query, rawText, Parse, request.Id));

        Assert.Equal(0, host.Model.TotalCalls);
    }

    /// <summary>EX-2: the operation above was added in this file, and nothing else changed.</summary>
    [Fact]
    public async Task A_new_operation_is_added_in_one_file_with_no_change_to_the_orchestrator()
    {
        Assert.Contains(Operations.All, static operation => operation.Name == "echo");
        Assert.Same(TestOperations.Echo, Operations.Named("echo"));

        using var host = new AgentsHost();
        host.Model.Answer("echo", """{"said":"hello"}""");
        var request = host.Recorder.Begin(Ulid.NewUlid(), "test");

        var result = await host.Runner.RunAsync(TestOperations.Echo,
            new AssembledContext(TestOperations.Echo.Instructions, "hello", EgressClass.Nothing), Parse, request.Id);

        Assert.Equal("hello", result.Value["said"]);
        Assert.Equal("saying it back", result.Step.Name);
    }

    // ---- The tools ------------------------------------------------------------------------------------

    /// <summary>D-5: an operation gets only its own tools, and a tool the catalogue lacks is refused.</summary>
    [Fact]
    public void An_operation_gets_only_its_own_tools()
    {
        var catalogue = new ToolCatalog()
            .Add(AIFunctionFactory.Create((string text) => text.ToUpperInvariant(), "shout", "Shouts."))
            .Add(AIFunctionFactory.Create((string text) => text.ToLowerInvariant(), "whisper", "Whispers."))
            .Add(AIFunctionFactory.Create(() => 42, "count", "Counts."));

        var scoped = catalogue.For(TestOperations.Shouting);

        Assert.Single(scoped);
        Assert.Equal("shout", scoped[0].Name);
        Assert.Empty(catalogue.For(TestOperations.Echo));
        Assert.Equal("tools: none", catalogue.Describe(TestOperations.Echo));
        Assert.Equal("tools:\n- shout: Shouts.", catalogue.Describe(TestOperations.Shouting));

        var unknown = TestOperations.Shouting with { Name = "whistling", Tools = ["whistle"] };
        var refused = Assert.Throws<UnknownToolException>(() => catalogue.For(unknown));
        Assert.Equal("whistle", refused.Tool);
    }

    [Fact]
    public void Every_shipped_operation_names_only_tools_the_catalogue_has()
    {
        using var host = new AgentsHost();

        foreach (var operation in Operations.All.Where(static operation => operation.Name is not "shouting"))
        {
            Assert.Equal(operation.Tools.Count, host.Tools.For(operation).Count);
        }

        // The measured rule (D-5): today no shipped operation carries a tool.
        foreach (var operation in Operations.All.Where(static operation => operation.Name is not "shouting" and not "echo"))
        {
            Assert.Empty(operation.Tools);
        }
    }

    // ---- AG-1f: impossible rather than forbidden ------------------------------------------------------------

    /// <summary>
    /// No type outside the orchestration assembly references the chat client type, so a public
    /// wrapper cannot reopen the hole. Read from the IL, so a lambda is held to the same rule.
    /// </summary>
    [Fact]
    public void No_type_outside_the_orchestration_assembly_references_the_chat_client_type()
    {
        var client = typeof(ChatModel);
        var offenders = new List<string>();

        foreach (var assembly in AssistantAssemblies)
        {
            foreach (var (owner, method) in IlReferences.Methods(assembly))
            {
                // This test is the one place outside that may name the type, to say that nothing else does.
                if (owner == typeof(OperationCatalogTests)) continue;

                foreach (var member in IlReferences.ReferencedMembers(method))
                {
                    if (member == client || member.DeclaringType == client)
                    {
                        offenders.Add($"{owner.FullName}.{method.Name} -> {member}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
        Assert.False(client.IsPublic, "the chat client type has become public");
    }

    /// <summary>
    /// The container hands the chat client to nothing but the runner: inside the assembly, only
    /// the runner and the registration touch it; and no registered service takes it by constructor.
    /// </summary>
    [Fact]
    public void The_container_hands_the_chat_client_to_nothing_but_the_runner()
    {
        var client = typeof(ChatModel);
        var allowed = new HashSet<Type> { client, typeof(OperationRunner), typeof(AssistantServices) };
        var offenders = new List<string>();

        foreach (var (owner, method) in IlReferences.Methods(client.Assembly))
        {
            if (allowed.Contains(owner)) continue;

            foreach (var member in IlReferences.ReferencedMembers(method))
            {
                if (member == client || member.DeclaringType == client)
                {
                    offenders.Add($"{owner.FullName}.{method.Name} -> {member}");
                }
            }
        }

        Assert.Empty(offenders);

        var services = new ServiceCollection()
            .AddScriptedModel(new ScriptedModel())
            .AddAssistantAgents()
            .AddSingleton<ITraceRecorder>(new MemoryTraceRecorder())
            .AddSingleton(new ToolCatalog());

        Assert.Single(services, descriptor => descriptor.ServiceType == client);

        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType is not { } implementation) continue;

            foreach (var constructor in implementation.GetConstructors())
            {
                Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == client);
            }
        }
    }

    private static Parsed<Dictionary<string, string>> Parse(string text)
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            return parsed is null ? Parsed<Dictionary<string, string>>.Invalid("not an object") : Parsed<Dictionary<string, string>>.Ok(parsed);
        }
        catch (System.Text.Json.JsonException failure)
        {
            return Parsed<Dictionary<string, string>>.Invalid(failure.Message);
        }
    }
}
