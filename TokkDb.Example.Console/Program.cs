using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using TokkDb;

class Tag {
  public string Name { get; set; }

  public Tag() { }
  public Tag(string name) { Name = name; }
}

class Passport {
  public string Code { get; set; }

  public Passport() { }
  public Passport(string code) => Code = code;
}

class Person {
  [Key]
  public int Id { get; set; }
  public string Name { get; set; }
  public int Age { get; set; }
  public Passport Passport { get; set; }
  public Tag[] Tags { get; set; }
}

internal class Program {
  public static void Main(string[] args) {
    var person1 = new Person {
      Id = 1,
      Name = "Oleksandr",
      Age = 29,
      Passport = new Passport("ST-111111"),
      Tags = [new Tag("tag1"), new Tag("tag2")]
    };
    var person2 = new Person {
      Id = 2,
      Name = "Kate",
      Age = 28,
      Passport = new Passport("ST-222222"),
      Tags = [new Tag("tag1"), new Tag("tag2")]
    };
    var db = new TokkDbConnection("/Users/ts/Student/db/temp/test.db");
    var persons = db.Entities<Person>("parsons");
    persons.Insert(person1);
    persons.Insert(person2);
    WriteAllPersons(persons);
    /*person1.Age++;
    person1.Tags = person1.Tags.Where(tag => tag.Name != "tag1").Concat([new Tag("Tag3")]).ToArray();
    persons.UpdateById(person1, 1);
    WriteAllPersons(persons);
    foreach (var history in persons.GetHistories()) {
      //todo
    }*/
  }

  private static void WriteAllPersons(DbEntities<Person> persons) {
    foreach (var record in persons.GetAll()) {
      Console.WriteLine(JsonSerializer.Serialize(record));
    }
  }
}
