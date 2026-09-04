namespace TokkDb;

public class TokkDbEntityConfiguration {
  //todo indexes...
}

public class TokkDbConfiguration {
  public Dictionary<string, TokkDbEntityConfiguration> Entities { get; set; } = [];

  internal TokkDbConfiguration() { }

  public TokkDbConfiguration CreateEntity<T>(string entityName = null) {
    entityName ??= typeof(T).Name;
    Entities.Add(entityName, new TokkDbEntityConfiguration());
    return this;
  }
}
