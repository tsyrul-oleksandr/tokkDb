namespace TokkDb.Benchmarks;

//The record every benchmark inserts. Small and flat, so the numbers measure the engine
//rather than the serializer.
public class Publication {
  public int Id { get; set; }
  public string Title { get; set; } = string.Empty;
  public string Doi { get; set; } = string.Empty;
  public int Year { get; set; }
  public Author? Author { get; set; }
  public Keyword[] Keywords { get; set; } = [];

  public static Publication Numbered(int i) {
    return new Publication {
      Id = i,
      Title = $"Publication {i}",
      Doi = $"10.1000/tokkdb.{i:D8}",
      Year = 1990 + i % 35,
      Author = new Author { Name = $"Author {i % 1000}" },
      Keywords = [new Keyword { Name = $"keyword-{i % 50}" }]
    };
  }
}

public class Author {
  public string Name { get; set; } = string.Empty;
}

public class Keyword {
  public string Name { get; set; } = string.Empty;
}
