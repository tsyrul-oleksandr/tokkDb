namespace TokkDb.LLM.Storage;

public interface ISemanticTypeRegistry
{
    void Register(SemanticTypeDefinition definition);

    bool Delete(string name);

    SemanticTypeDefinition? GetByNameOrAlias(string nameOrAlias);

    IReadOnlyCollection<SemanticTypeDefinition> GetAll();
}
