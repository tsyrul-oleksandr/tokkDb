namespace TokkDb.LLM.Core;

public sealed record UserInteractionRequest(
    string RequestId,
    string Message,
    IReadOnlyCollection<UserAction> Actions,
    DateTimeOffset CreatedUtc);
