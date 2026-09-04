namespace TokkDb.LLM.Core;

public sealed class MemorySchemaChangeProposalStore : ISchemaChangeProposalStore
{
    public List<SchemaChangeProposal> Proposals { get; } = new();
    
    public IReadOnlyCollection<SchemaChangeProposal> GetAll()
    {
        return Proposals;
    }

    public SchemaChangeProposal? GetById(string proposalId)
    {
        return Proposals.
            FirstOrDefault(item => string.Equals(item.ProposalId, proposalId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void Save(SchemaChangeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        Proposals.Add(proposal);
    }

    public void Clear()
    {
        Proposals.Clear();
    }
}
