using Microsoft.Agents.AI.Workflows;

namespace TokkDb.LLM.Core.Orchestration.Workflows;

/// <summary>
/// Builds the chat workflow graph:
/// <code>
/// ChatAgentExecutor  ──►  ChatUserDecision (RequestPort)
///          ▲                       │
///          └───────────────────────┘
/// </code>
/// The request port pauses the workflow and surfaces a decision request to the
/// application; the response resumes the same executor.
/// </summary>
internal static class ChatWorkflowFactory
{
    public const string DecisionPortId = "ChatUserDecision";

    public static Workflow Build(IConversationAgent conversationAgent, ConversationRequest configuration)
    {
        var chatExecutor = new ChatAgentExecutor(conversationAgent, configuration);
        var decisionPort = RequestPort.Create<ChatDecisionPrompt, ChatAgentInput>(DecisionPortId);

        var builder = new WorkflowBuilder(chatExecutor);
        builder.AddEdge(chatExecutor, decisionPort);
        builder.AddEdge(decisionPort, chatExecutor);
        builder.WithOutputFrom(chatExecutor);
        builder.WithName("TokkDbChatWorkflow");
        builder.WithDescription("Chat agent with human-in-the-loop decision port.");

        return builder.Build();
    }
}
