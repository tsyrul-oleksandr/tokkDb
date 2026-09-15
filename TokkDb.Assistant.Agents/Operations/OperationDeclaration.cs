using TokkDb.Assistant.Agents.Models;

namespace TokkDb.Assistant.Agents.Operations;

/// <summary>
/// One named operation (AG-1, AG-1a, AG-7, NF-4a, D-4, D-5, EX-2): what it is called, what the
/// diagram calls its step, the instructions it runs under, the model configuration it may
/// override, the tools it is allowed - a subset of the catalogue, usually empty - the context
/// budget the assembler enforces before the call, the output cap the transport enforces, what
/// of the user's it sends, and the shape of its answer.
///
/// <b>Every model call is one of these.</b> There is no other way to reach the model: the runner
/// takes a declaration, the chat client type is internal, and the runner is the only place it is
/// resolved (AG-1f). Adding an operation is adding one of these in one file and nothing else
/// (EX-2); <see cref="Operations"/> finds it by reflection.
/// </summary>
/// <param name="Name">Short, stable, lower case: the key of the fake's script and of a settings override.</param>
/// <param name="StepName">What the diagram calls the step, in plain words.</param>
/// <param name="Instructions">The system instructions, fixed for the life of the operation (AG-6a).</param>
/// <param name="Tools">Names in the <see cref="ToolCatalog"/>. Empty for most: the first tool costs more than a digest (D-5).</param>
/// <param name="ContextBudget">Prompt tokens the assembled context may take, enforced before the call (AG-1a). From §6.2.</param>
/// <param name="OutputBudget">Completion tokens the transport lets the model produce (R-2b).</param>
/// <param name="Egress">What of the user's the operation may send (NF-4a).</param>
/// <param name="OutputSchema">The JSON schema of the answer, or null for free text.</param>
/// <param name="Model">A configuration of its own, or null for the settings' default (AG-7).</param>
/// <param name="MaxToolIterations">How many times the tool loop may go round (R-1's condition).</param>
/// <param name="MaxRepairs">How many malformed answers may be sent back before the operation fails (AG-5).</param>
public sealed record OperationDeclaration(
    string Name,
    string StepName,
    string Instructions,
    IReadOnlyList<string> Tools,
    int ContextBudget,
    int OutputBudget,
    EgressClass Egress,
    string? OutputSchema,
    ModelConfiguration? Model = null,
    int MaxToolIterations = 2,
    int MaxRepairs = 2)
{
    public bool HasTools => Tools.Count > 0;

    public bool IsStructured => OutputSchema is not null;
}
