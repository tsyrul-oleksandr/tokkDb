using TokkDb.Query.Tokenizers.Conditions;
using TokkDb.Query.Tokenizers.Readers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Tokenizers;

// $.name = '123'

public class QueryTokenizer {
  public TokenReader Reader { get; set; }
  
  protected static ITokenCondition[] Conditions = [
    new EqualCharCondition('.', TokenType.Dot),
    new EqualCharCondition('$', TokenType.Root),
    new EqualStringCondition("count", TokenType.Word),
    new WordCondition()
  ];

  public QueryTokenizer(string value) {
    Reader = new TokenReader(value);
  }

  public virtual Token NextToken() {
    if (Reader.IsEnd()) {
      return null;
    }
    foreach (var condition in Conditions) {
      if (condition.Match(Reader, out var value)) {
        return new Token(condition.Type, value);
      }
    }
    throw new NotImplementedException("Token not found");
  }
}
