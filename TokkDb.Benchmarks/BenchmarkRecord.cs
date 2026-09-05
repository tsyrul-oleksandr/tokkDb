namespace TokkDb.Benchmarks;

//The record every benchmark inserts. Small and flat, so the numbers measure the engine
//rather than the serializer.
public class Publication {
  public int Id { get; set; }
  public string Title { get; set; } = string.Empty;
  public string Doi { get; set; } = string.Empty;
  public int Year { get; set; }
  //DC-4 names the fields a secondary index has to cover: title, authors, year, DOI,
  //keywords, institution and document type. These two are here so that the index-count
  //benchmark has five scalar columns to index rather than four.
  public string Institution { get; set; } = string.Empty;
  public string DocumentType { get; set; } = string.Empty;
  public Author? Author { get; set; }
  public Keyword[] Keywords { get; set; } = [];

  public static Publication Numbered(int i) {
    return new Publication {
      Id = i,
      Title = $"Publication {i}",
      Doi = $"10.1000/tokkdb.{i:D8}",
      Year = 1990 + i % 35,
      Institution = $"Institution {i % 200}",
      DocumentType = i % 3 == 0 ? "article" : i % 3 == 1 ? "thesis" : "dataset",
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
