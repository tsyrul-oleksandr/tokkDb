using TokkDb.Pages;

namespace TokkDb;

public class TokkDbEntityConfiguration {
  public Type EntityType { get; }
  public string Description { get; set; } = string.Empty;
  //todo indexes...

  public TokkDbEntityConfiguration(Type entityType) {
    EntityType = entityType;
  }
}

public class TokkDbConfiguration {
  public Dictionary<string, TokkDbEntityConfiguration> Entities { get; set; } = [];

  internal TokkDbConfiguration() { }

  public TokkDbConfiguration CreateEntity<T>(string entityName = null, string description = "") {
    entityName ??= typeof(T).Name;
    if (SystemCollections.IsReservedName(entityName)) {
      throw new ReservedCollectionNameException(entityName);
    }
    Entities.Add(entityName, new TokkDbEntityConfiguration(typeof(T)) { Description = description });
    return this;
  }
}
