namespace TokkDb.DevConsole.Model;

public class User {
  public Ulid Id { get; set; }
  public string Name { get; set; }
  public string Email { get; set; }
  public string Password { get; set; }
}
