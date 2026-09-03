namespace TokkDb.Tests;

public class Tag {
  public string Name { get; set; }

  public Tag() { }
  public Tag(string name) => Name = name;
}

public class Passport {
  public string Code { get; set; }

  public Passport() { }
  public Passport(string code) => Code = code;
}

public class Person {
  public int Id { get; set; }
  public string Name { get; set; }
  public int Age { get; set; }
  public Passport Passport { get; set; }
  public Tag[] Tags { get; set; }
}

public static class TestPeople {
  public static Person Ivan() {
    return new Person {
      Id = 1, Name = "Ivan", Age = 29,
      Passport = new Passport("ST-111111"),
      Tags = [new Tag("tag1"), new Tag("tag2")]
    };
  }

  public static Person Numbered(int i) {
    return new Person {
      Id = i, Name = $"Person-{i}", Age = 20 + i % 40,
      Passport = new Passport($"ST-{i:D6}"),
      Tags = [new Tag($"tag-{i}")]
    };
  }
}
