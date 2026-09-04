namespace TokkDb.LLM.Core;

public interface ISchemaChangeProposalStore
{
    IReadOnlyCollection<SchemaChangeProposal> GetAll();

    SchemaChangeProposal? GetById(string proposalId);

    void Save(SchemaChangeProposal proposal);

    void Clear();
}
