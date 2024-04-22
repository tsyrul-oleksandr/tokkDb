using TokkDb.Query.Tokenizers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Builders;

public class SelectionBuilder {
  public void Build(string value) {
    var reader = new QueryTokenizer(value);
    var token = reader.NextToken();
    if (token.Type != TokenType.Root) {
      throw new Exception("Root token not found");
    }
    while ((token = reader.NextToken()) != null) {
      if (token.Type == TokenType.Dot) {
        
      }
    }
  }
}
