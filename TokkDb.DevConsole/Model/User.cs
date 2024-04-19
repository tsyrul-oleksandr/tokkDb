using System.ComponentModel.DataAnnotations;

namespace TokkDb.DevConsole.Model;

public class User {
  [Key]
  public int Id { get; set; }
  public string Name { get; set; }
  public string Email { get; set; }
  public int Age { get; set; }
  public string Password { get; set; }
}
