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
      Name = "Ivan",
      Age = 29,
      Passport = new Passport("ST-111111"),
      Tags = [new Tag("tag1"), new Tag("tag2")]
    };
    var person2 = new Person {
      Id = 2,
      Name = "Pavlo",
      Age = 28,
      Passport = new Passport("ST-222222"),
      Tags = [new Tag("tag1"), new Tag("tag3"), new Tag("ST-222222")]
    };
    var db = new TokkDbConnection("/Users/ts/Student/db/temp/test.db");
    if (!db.IsExists()) {
      db.CreateDatabase((conf) => 
        conf.CreateEntity<Person>()
      );
      var initPersons = db.Entities<Person>();
      initPersons.Insert(person1);
      initPersons.Insert(person2);
    } else {
      db.Load();
    }
    var persons = db.Entities<Person>();
    WriteAllPersons(persons.GetAll());

    WriteAllPersons(persons.Get("$.Passport.Code"));
    WriteAllPersons(persons.Get("[($.Age + 1) == 29].Tags[$.Passport.Code == @.Name]"));
    //persons.Find("$[?($.Passport == 'ST-222222')]");
    
    
    
    /*person1.Age++;
    person1.Tags = person1.Tags.Where(tag => tag.Name != "tag1").Concat([new Tag("Tag3")]).ToArray();
    persons.UpdateById(person1, 1);
    WriteAllPersons(persons);
    foreach (var history in persons.GetHistories()) {
    
    }*/
  }

  private static void WriteAllPersons(IEnumerable<Person> persons) {
    foreach (var record in persons) {
      Console.WriteLine(JsonSerializer.Serialize(record));
    }
  }
}
