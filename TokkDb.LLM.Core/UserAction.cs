using System.ComponentModel;

namespace TokkDb.LLM.Core;

/// <summary>One choice offered to the user when a workflow needs a decision.</summary>
public sealed record UserAction(
    [property: Description("Stable identifier, lowercase and without spaces, such as 'approve' or 'reject'.")]
    string Id,

    [property: Description("Label shown on the button, such as 'Approve'.")]
    string Title,

    [property: Description("Optional sentence explaining what choosing this does.")]
    string? Description = null);
