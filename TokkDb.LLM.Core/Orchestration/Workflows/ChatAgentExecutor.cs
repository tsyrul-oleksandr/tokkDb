using Microsoft.Agents.AI.Workflows;
using System.Text;

namespace TokkDb.LLM.Core.Orchestration.Workflows;

/// <summary>
/// Workflow executor that runs the main Chat Agent. It never touches storage
/// directly: the agent reaches the application only through controlled tools
/// registered by <see cref="IConversationAgent"/>.
/// </summary>
[SendsMessage(typeof(ChatDecisionPrompt))]
[YieldsOutput(typeof(ChatTurnOutput))]
internal sealed partial class ChatAgentExecutor : Executor<ChatAgentInput>
{
    public const string ExecutorId = "ChatAgent";

    private readonly IConversationAgent _conversationAgent;
    private readonly ConversationRequest _configuration;

    private string? _initialMessage;
    private string? _lastAgentResponse;

    public ChatAgentExecutor(IConversationAgent conversationAgent, ConversationRequest configuration)
        : base(ExecutorId)
    {
        ArgumentNullException.ThrowIfNull(conversationAgent);
        ArgumentNullException.ThrowIfNull(configuration);
        _conversationAgent = conversationAgent;
        _configuration = configuration;
    }

    public override async ValueTask HandleAsync(
        ChatAgentInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var prompt = BuildPrompt(message);
        _initialMessage ??= prompt;

        var response = await _conversationAgent
            .SendAsync(_configuration with { Message = prompt }, cancellationToken)
            .ConfigureAwait(false);

        _lastAgentResponse = response.Text;

        if (response.UserInteractionRequest is not null)
        {
            var interaction = response.UserInteractionRequest;
            await context.SendMessageAsync(
                new ChatDecisionPrompt(
                    interaction.RequestId,
                    interaction.Message,
                    interaction.Actions.ToArray()),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await context.YieldOutputAsync(
            new ChatTurnOutput(response.Text ?? string.Empty, response.Usage),
            cancellationToken).ConfigureAwait(false);
    }

    private string BuildPrompt(ChatAgentInput input)
    {
        if (input.Decision is null)
        {
            return input.Message ?? string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Resume the paused workflow.");
        if (!string.IsNullOrWhiteSpace(_initialMessage))
        {
            builder.AppendLine($"Original user message: {_initialMessage}");
        }

        if (!string.IsNullOrWhiteSpace(_lastAgentResponse))
        {
            builder.AppendLine("Last agent response:");
            builder.AppendLine(_lastAgentResponse);
        }

        builder.AppendLine("User decision:");
        builder.AppendLine($"Decision: {input.Decision}");
        if (!string.IsNullOrWhiteSpace(input.ActionId))
        {
            builder.AppendLine($"ActionId: {input.ActionId}");
        }

        if (!string.IsNullOrWhiteSpace(input.ActionTitle))
        {
            builder.AppendLine($"ActionTitle: {input.ActionTitle}");
        }

        if (!string.IsNullOrWhiteSpace(input.ActionDescription))
        {
            builder.AppendLine($"ActionDescription: {input.ActionDescription}");
        }

        if (!string.IsNullOrWhiteSpace(input.AdditionalInstructions))
        {
            builder.AppendLine("Additional instructions:");
            builder.AppendLine(input.AdditionalInstructions.Trim());
        }

        builder.AppendLine("Continue from the current workflow state.");
        return builder.ToString();
    }
}
