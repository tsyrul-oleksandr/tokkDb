using TokkDb.Query.Tokenizers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Builders;

// $.name
public class SelectionBuilder {
  public void Build(string value) {
    var reader = new QueryTokenizer(value);
    while (reader.NextToken() is { } token) {
      if (token.Type == TokenType.Dot) {
        
      }
      if (token.Type == TokenType.StartRoundBracket) {
        
      }
    }
  }

  protected virtual void BuildGroup(QueryTokenizer tokenizer) {
    
  }
}
