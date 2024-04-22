namespace TokkDb.Query.Tokenizers.Tokens;

public class Token {
  public TokenType Type { get; set; }
  public string Value { get; set; }
  public Token(TokenType type, string value) {
    Type = type;
    Value = value;
  }
}
